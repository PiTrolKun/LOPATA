using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;

namespace AIHub.Services;

public sealed class SpecialistComponentToolService
{
    private const int MaximumAnalysisDimension = 256;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ComponentManager _componentManager;

    public SpecialistComponentToolService(ComponentManager? componentManager = null)
    {
        _componentManager = componentManager ?? new ComponentManager();
    }

    public string InspectImagePixels(SessionFileManifest manifest, string fileId)
    {
        var file = ResolveFile(manifest, fileId, SessionFileCategories.Image);
        using var stream = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new SessionFileToolException(
                "image_decode_failed",
                "The attached image has no decodable frame.");
        var scale = Math.Min(
            1d,
            MaximumAnalysisDimension
            / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
        BitmapSource sampled = scale < 1d
            ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
            : frame;
        var converted = new FormatConvertedBitmap(
            sampled,
            PixelFormats.Bgra32,
            null,
            0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        long blue = 0;
        long green = 0;
        long red = 0;
        long alpha = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            blue += pixels[index];
            green += pixels[index + 1];
            red += pixels[index + 2];
            alpha += pixels[index + 3];
        }

        var count = Math.Max(1, pixels.Length / 4);
        var averageRed = (int)(red / count);
        var averageGreen = (int)(green / count);
        var averageBlue = (int)(blue / count);
        var averageAlpha = (int)(alpha / count);
        var brightness = Math.Round(
            (0.2126 * averageRed + 0.7152 * averageGreen + 0.0722 * averageBlue)
            / 255d,
            3);
        return JsonSerializer.Serialize(new
        {
            success = true,
            file_id = file.Id,
            width = frame.PixelWidth,
            height = frame.PixelHeight,
            dpi_x = Math.Round(frame.DpiX, 2),
            dpi_y = Math.Round(frame.DpiY, 2),
            source_format = frame.Format.ToString(),
            sampled_width = converted.PixelWidth,
            sampled_height = converted.PixelHeight,
            average_color = new
            {
                red = averageRed,
                green = averageGreen,
                blue = averageBlue,
                alpha = averageAlpha,
                hex = $"#{averageRed:X2}{averageGreen:X2}{averageBlue:X2}"
            },
            normalized_brightness = brightness,
            semantic_content_understood = false,
            limitation = "This adapter reports deterministic pixel properties only. It does not identify people, objects, text or meaning."
        }, JsonOptions);
    }

    public async Task<string> InspectImageExtendedAsync(
        SessionFileManifest manifest,
        string fileId,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        var file = ResolveFile(manifest, fileId, SessionFileCategories.Image);
        var executable = ResolveProcessingComponentArtifact("runtime.imagemagick");
        var startInfo = CreateToolStartInfo(executable);
        startInfo.ArgumentList.Add("identify");
        startInfo.ArgumentList.Add("-format");
        startInfo.ArgumentList.Add("%w|%h|%m|%z|%[colorspace]|%Q");
        startInfo.ArgumentList.Add(file.SourcePath);
        var result = await RunProcessAsync(startInfo, cancellationToken);
        EnsureProcessSucceeded(
            result,
            "imagemagick_inspect_failed",
            "ImageMagick could not inspect the attached image.");
        var fields = result.StandardOutput.Trim().Split('|');
        return JsonSerializer.Serialize(new
        {
            success = true,
            file_id = file.Id,
            width = ParsePositiveInteger(fields, 0),
            height = ParsePositiveInteger(fields, 1),
            format = GetField(fields, 2),
            bit_depth = ParsePositiveInteger(fields, 3),
            color_space = GetField(fields, 4),
            quality = ParsePositiveInteger(fields, 5),
            runtime = "ImageMagick",
            semantic_content_understood = false,
            limitation = "This adapter reports verified image metadata only. It does not identify objects, people, text or scene meaning."
        }, JsonOptions);
    }

    public async Task<string> ExtractImageTextAsync(
        SessionFileManifest manifest,
        string fileId,
        string language,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        var file = ResolveFile(manifest, fileId, SessionFileCategories.Image);
        var executable = ResolveProcessingComponentArtifact("runtime.tesseract");
        var normalizedLanguage = NormalizeTesseractLanguage(language);
        var startInfo = CreateToolStartInfo(executable);
        startInfo.ArgumentList.Add(file.SourcePath);
        startInfo.ArgumentList.Add("stdout");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(normalizedLanguage);
        var result = await RunProcessAsync(startInfo, cancellationToken);
        EnsureProcessSucceeded(
            result,
            "tesseract_ocr_failed",
            "Tesseract could not extract text from the attached image.");
        return JsonSerializer.Serialize(new
        {
            success = true,
            file_id = file.Id,
            language = normalizedLanguage,
            text = result.StandardOutput.Trim(),
            runtime = "Tesseract OCR",
            semantic_content_understood = false,
            limitation = "OCR extracts printed characters only. It does not identify general objects or infer scene meaning."
        }, JsonOptions);
    }

    public async Task<string> TransformImageAsync(
        SessionFileManifest manifest,
        string fileId,
        StorageSettings storageSettings,
        string outputFormat,
        int? width,
        int? height,
        string fit,
        bool stripMetadata,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        var file = ResolveFile(manifest, fileId, SessionFileCategories.Image);
        var executable = ResolveProcessingComponentArtifact("runtime.imagemagick");
        var normalizedFormat = NormalizeImageFormat(outputFormat);
        var normalizedFit = NormalizeImageFit(fit);
        ValidateImageDimension(width, nameof(width));
        ValidateImageDimension(height, nameof(height));
        var artifactRoot = ResolveArtifactRoot(storageSettings);
        var outputDirectory = Path.Combine(
            artifactRoot,
            DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(
            outputDirectory,
            $"image_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.{normalizedFormat}");
        var startInfo = CreateToolStartInfo(executable);
        startInfo.ArgumentList.Add(file.SourcePath);
        AddResizeArguments(startInfo, width, height, normalizedFit);
        if (stripMetadata)
        {
            startInfo.ArgumentList.Add("-strip");
        }

        startInfo.ArgumentList.Add(outputPath);
        var result = await RunProcessAsync(startInfo, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            TryDeleteFile(outputPath);
            EnsureProcessSucceeded(
                result,
                "imagemagick_transform_failed",
                "ImageMagick could not create the requested image artifact.");
            throw new SessionFileToolException(
                "imagemagick_transform_failed",
                "ImageMagick did not create the requested image artifact.");
        }

        var relativeReference = Path.GetRelativePath(artifactRoot, outputPath)
            .Replace('\\', '/');
        return JsonSerializer.Serialize(new
        {
            success = true,
            source_file_id = file.Id,
            artifact_reference = relativeReference,
            file_name = Path.GetFileName(outputPath),
            output_format = normalizedFormat,
            width,
            height,
            fit = normalizedFit,
            metadata_stripped = stripMetadata,
            source_modified = false,
            runtime = "ImageMagick"
        }, JsonOptions);
    }

    public async Task<string> TranscribeAudioAsync(
        SessionFileManifest manifest,
        string fileId,
        string language,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        var file = ResolveFile(manifest, fileId, SessionFileCategories.Audio);
        var statuses = _componentManager.GetStatus(ComponentKinds.Processing)
            .ToDictionary(status => status.Entry.Id, StringComparer.OrdinalIgnoreCase);
        var runtime = RequireAvailableComponent(statuses, "runtime.whisper.cpu");
        var model = RequireAvailableComponent(statuses, "model.whisper.small");
        var executable = ResolveInstalledArtifact(runtime);
        var modelPath = ResolveInstalledArtifact(model);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "AI_HUB",
            "Whisper",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        var outputBase = Path.Combine(outputDirectory, "transcript");
        var outputPath = outputBase + ".txt";
        try
        {
            using var process = new Process
            {
                StartInfo = CreateWhisperStartInfo(
                    executable,
                    modelPath,
                    file.SourcePath,
                    outputBase,
                    language)
            };
            if (!OwnedProcessRegistry.Shared.Start(process, "SpecialistComponentToolService"))
            {
                throw new SessionFileToolException(
                    "whisper_start_failed",
                    "The verified Whisper runtime could not be started.");
            }

            await process.WaitForExitAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new SessionFileToolException(
                    "whisper_failed",
                    string.IsNullOrWhiteSpace(standardError)
                        ? "Whisper did not produce a transcript."
                        : $"Whisper did not produce a transcript: {Limit(standardError, 500)}");
            }

            var transcript = await File.ReadAllTextAsync(
                outputPath,
                Encoding.UTF8,
                cancellationToken);
            return JsonSerializer.Serialize(new
            {
                success = true,
                file_id = file.Id,
                language = string.IsNullOrWhiteSpace(language) ? "auto" : language,
                transcript,
                runtime = runtime.Entry.Name,
                model = model.Entry.Name
            }, JsonOptions);
        }
        finally
        {
            TryDeleteDirectory(outputDirectory);
        }
    }

    private static ProcessStartInfo CreateWhisperStartInfo(
        string executable,
        string modelPath,
        string inputPath,
        string outputBase,
        string language)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("-otxt");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add(outputBase);
        if (!string.IsNullOrWhiteSpace(language)
            && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(language.Trim().ToLowerInvariant());
        }

        return startInfo;
    }

    private string ResolveProcessingComponentArtifact(string componentId)
    {
        var statuses = _componentManager.GetStatus(ComponentKinds.Processing)
            .ToDictionary(status => status.Entry.Id, StringComparer.OrdinalIgnoreCase);
        return ResolveInstalledArtifact(RequireAvailableComponent(statuses, componentId));
    }

    private static ProcessStartInfo CreateToolStartInfo(string executable) =>
        new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

    private static async Task<ProcessExecutionResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!OwnedProcessRegistry.Shared.Start(process, "SpecialistComponentToolService"))
        {
            throw new SessionFileToolException(
                "component_start_failed",
                "The verified processing component could not be started.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessExecutionResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static void EnsureProcessSucceeded(
        ProcessExecutionResult result,
        string errorCode,
        string safeMessage)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        throw new SessionFileToolException(
            errorCode,
            string.IsNullOrWhiteSpace(result.StandardError)
                ? safeMessage
                : $"{safeMessage} {Limit(result.StandardError.Trim(), 500)}");
    }

    private static string NormalizeTesseractLanguage(string language)
    {
        var normalized = string.IsNullOrWhiteSpace(language)
            ? "eng"
            : language.Trim().ToLowerInvariant();
        if (normalized.Length > 32
            || normalized.Any(character =>
                !char.IsAsciiLetter(character)
                && character is not '+' and not '_' and not '-'))
        {
            throw new SessionFileToolException(
                "invalid_argument",
                "The OCR language must contain only short ISO language codes such as eng, rus, or eng+rus.");
        }

        return normalized;
    }

    private static string NormalizeImageFormat(string outputFormat)
    {
        var normalized = string.IsNullOrWhiteSpace(outputFormat)
            ? "png"
            : outputFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (normalized == "jpeg")
        {
            normalized = "jpg";
        }

        return normalized is "png" or "jpg" or "webp" or "tiff" or "bmp"
            ? normalized
            : throw new SessionFileToolException(
                "unsupported_output_format",
                "The output format must be png, jpg, webp, tiff, or bmp.");
    }

    private static string NormalizeImageFit(string fit)
    {
        var normalized = string.IsNullOrWhiteSpace(fit)
            ? "contain"
            : fit.Trim().ToLowerInvariant();
        return normalized is "contain" or "cover" or "stretch"
            ? normalized
            : throw new SessionFileToolException(
                "invalid_argument",
                "The image fit mode must be contain, cover, or stretch.");
    }

    private static void ValidateImageDimension(int? value, string name)
    {
        if (value is <= 0 or > 32768)
        {
            throw new SessionFileToolException(
                "invalid_argument",
                $"The image dimension '{name}' must be between 1 and 32768.");
        }
    }

    private static void AddResizeArguments(
        ProcessStartInfo startInfo,
        int? width,
        int? height,
        string fit)
    {
        if (width is null && height is null)
        {
            return;
        }

        var geometry = $"{width?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}x{height?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";
        startInfo.ArgumentList.Add("-resize");
        startInfo.ArgumentList.Add(fit switch
        {
            "stretch" when width is not null && height is not null => geometry + "!",
            "cover" when width is not null && height is not null => geometry + "^",
            _ => geometry
        });
        if (fit == "cover" && width is not null && height is not null)
        {
            startInfo.ArgumentList.Add("-gravity");
            startInfo.ArgumentList.Add("center");
            startInfo.ArgumentList.Add("-extent");
            startInfo.ArgumentList.Add(geometry);
        }
    }

    private static string ResolveArtifactRoot(StorageSettings storageSettings)
    {
        var configuredRoot = storageSettings.Results.Locations
            .Select(location => location.Path?.Trim())
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppDataPaths.RuntimeDirectory, "Sandbox", "Artifacts")
            : Path.Combine(configuredRoot, "AI_HUB", "Sandbox", "Artifacts");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static int? ParsePositiveInteger(IReadOnlyList<string> fields, int index) =>
        index < fields.Count
        && int.TryParse(
            fields[index],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
        && value >= 0
            ? value
            : null;

    private static string GetField(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index].Trim() : string.Empty;

    private static SessionFileReference ResolveFile(
        SessionFileManifest manifest,
        string fileId,
        string expectedCategory)
    {
        var file = manifest.Files.FirstOrDefault(item =>
            string.Equals(item.Id, fileId, StringComparison.Ordinal));
        if (file is null || !file.IsAvailable || !File.Exists(file.SourcePath))
        {
            throw new SessionFileToolException(
                "file_unavailable",
                "The requested attached file is unavailable.");
        }

        if (!string.Equals(file.Category, expectedCategory, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionFileToolException(
                "unsupported_file_category",
                $"This tool requires an attached {expectedCategory} file.");
        }

        return file;
    }

    private static ComponentStatusSnapshot RequireAvailableComponent(
        IReadOnlyDictionary<string, ComponentStatusSnapshot> statuses,
        string componentId)
    {
        if (!statuses.TryGetValue(componentId, out var status) || !status.IsAvailable)
        {
            throw new SessionFileToolException(
                "component_unavailable",
                $"The trusted component '{componentId}' is not installed and verified.");
        }

        return status;
    }

    private static string ResolveInstalledArtifact(ComponentStatusSnapshot status)
    {
        if (File.Exists(status.Record.InstallPath))
        {
            return Path.GetFullPath(status.Record.InstallPath);
        }

        var path = Path.Combine(
            status.Record.InstallPath,
            status.Entry.HealthCheckRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new SessionFileToolException(
                "component_health_check_failed",
                $"The verified component '{status.Entry.Id}' is missing its expected artifact.");
        }

        return Path.GetFullPath(path);
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary output is best-effort cleanup.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Incomplete artifacts are best-effort cleanup.
        }
    }

    private sealed record ProcessExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
