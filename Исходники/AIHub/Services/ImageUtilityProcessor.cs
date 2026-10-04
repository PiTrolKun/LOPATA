using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ImageUtilityProcessor : IImageUtilityProcessor
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _magickPath;
    private readonly IImageUtilityAiProcessor? _ai;
    public string ExecutablePath => _magickPath;
    public bool IsAvailable => File.Exists(_magickPath);

    public ImageUtilityProcessor(string? magickPath = null, IImageUtilityAiProcessor? ai = null)
    {
        _magickPath = magickPath ?? Path.Combine(AppContext.BaseDirectory, "ImageUtilityRuntime", "ImageMagick", "magick.exe");
        _ai = ai;
    }

    public async Task<IReadOnlyList<ImageUtilityFormat>> GetFormatsAsync(CancellationToken token = default)
        => ImageUtilityFormats.FromRuntimeList(await RunAsync(["-list", "format"], token));

    public async Task<ImageUtilityImageInfo> InspectAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new ImageUtilityException("ImageUtility.Error.SourceMissing");
        var temporary = Path.Combine(Path.GetTempPath(), "lopata-inspect-" + Guid.NewGuid().ToString("N") + SafeInputExtension(path));
        try
        {
            await CopyAsync(path, temporary, token);
            return await InspectControlledPathAsync(temporary, token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<ImageUtilityImageInfo> InspectControlledPathAsync(string path, CancellationToken token)
    {
        var text = await RunAsync(["identify", "-ping", "-format", "%w|%h|%n|%m|%[orientation]|%W|%H\n", path], token);
        var fields = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().Split('|');
        if (fields is not { Length: >= 4 } || !int.TryParse(fields[0], out var width)
            || !int.TryParse(fields[1], out var height) || !int.TryParse(fields[2], out var frames)
            || width < 1 || height < 1 || frames < 1)
            throw new ImageUtilityException("ImageUtility.Error.InvalidImage");
        if (frames > 1 && fields.Length >= 7 && int.TryParse(fields[5], out var canvasWidth)
            && int.TryParse(fields[6], out var canvasHeight) && canvasWidth > 0 && canvasHeight > 0)
        { width = canvasWidth; height = canvasHeight; }
        if (fields.Length >= 5 && fields[4] is "LeftTop" or "RightTop" or "RightBottom" or "LeftBottom")
            (width, height) = (height, width);
        return new(width, height, frames, fields[3]);
    }

    public async Task<string> ProcessAsync(ImageUtilityItem item, ImageUtilityOptions options, string outputFolder,
        IProgress<ImageUtilityProgress>? progress = null, CancellationToken token = default, Action? checkpoint = null)
    {
        var method = ImageUtilityCatalog.GetMethod(options.MethodId);
        var format = ImageUtilityFormats.Get(options.Format);
        ValidateOptions(options);
        var folder = ResolveOutputFolder(outputFolder, item.RelativeFolder);
        Directory.CreateDirectory(folder);
        var scratch = Path.Combine(Path.GetTempPath(), "LOPATA", "ImageUtility", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var temporaryOutput = Path.Combine(folder, ".lopata-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var source = await PrepareSourceAsync(item, scratch, progress, token);
            if (IsAnimatedPng(source)) throw new ImageUtilityException("ImageUtility.Error.ApngUnsupported");
            var info = await InspectControlledPathAsync(source, token);
            if (info.Format is "PSD" or "PSB")
            {
                // PSD stores the composited image before its layers. Reading it is a single still image.
                var flattened = Path.Combine(scratch, "composite.miff");
                await RunAsync([source + "[0]", flattened], token);
                source = flattened;
                info = await InspectControlledPathAsync(source, token);
            }
            if (info.Format == "FITS" && info.Frames == 3)
            {
                var combined = Path.Combine(scratch, "rgb.miff");
                await RunAsync([source, "-set", "colorspace", "sRGB", "-combine", combined], token);
                source = combined;
                info = await InspectControlledPathAsync(source, token);
            }
            item.Width = info.Width; item.Height = info.Height; item.Frames = info.Frames;
            var size = ImageUtilityDimensions.Calculate(info.Width, info.Height, options);
            item.OutputWidth = size.Width; item.OutputHeight = size.Height;
            if (format.Id == "ico" && (size.Width > 256 || size.Height > 256))
                throw new ImageUtilityException("ImageUtility.Error.IconSize");
            if (!options.FormatOnly && method.Id == "area" && (size.Width > info.Width || size.Height > info.Height))
                throw new ImageUtilityException("ImageUtility.Error.AreaEnlarge");
            if (info.Frames > 1 && !options.FormatOnly && method.IsAi)
                throw new ImageUtilityException("ImageUtility.Error.AiAnimation");
            if (info.Frames > 1 && !format.SupportsAnimation)
                throw new ImageUtilityException("ImageUtility.Error.AnimationFormat");
            if (info.Frames > 1 && info.Format is not ("GIF" or "WEBP" or "MNG" or "APNG" or "PNG"))
                throw new ImageUtilityException("ImageUtility.Error.MultiPage");
            progress?.Report(new("ImageUtility.Event.Processing", [item.DisplayName, info.Width, info.Height, size.Width, size.Height], ItemId: item.Id));

            var input = source;
            if (!options.FormatOnly && method.IsAi)
            {
                if (_ai is null) throw new ImageUtilityException("ImageUtility.Error.AiMissing");
                var normalized = Path.Combine(scratch, "input.png");
                await RunAsync([source, "-auto-orient", "-colorspace", "sRGB", "-depth", "8", "PNG32:" + normalized], token);
                input = Path.Combine(scratch, "upscaled.png");
                await _ai.ProcessAsync(method.Id, normalized, input, options.Parameters, progress, token);
                if (!File.Exists(input)) throw new ImageUtilityException("ImageUtility.Error.NoResult", retryable: true);
            }
            var arguments = new List<string> { input, "-auto-orient" };
            if (info.Frames > 1) arguments.Add("-coalesce");
            if (!options.FormatOnly)
            {
                AddResizeArguments(arguments, options, size.Width, size.Height, method.IsAi);
                if (options.Sharpen > 0) arguments.AddRange(["-unsharp", "0x1+" + Number(options.Sharpen) + "+0.02"]);
            }
            if (!options.FormatOnly && method.IsAi && options.PreserveTransparency && format.SupportsAlpha)
            {
                var alpha = Path.Combine(scratch, "alpha.png");
                await RunAsync([source, "-auto-orient", "-alpha", "extract", "-filter", "Lanczos", "-resize",
                    FormattableString.Invariant($"{size.Width}x{size.Height}!"), alpha], token);
                arguments.AddRange([alpha, "-alpha", "off", "-compose", "CopyOpacity", "-composite"]);
            }
            if (!options.PreserveTransparency || !format.SupportsAlpha)
                arguments.AddRange(["-background", options.BackgroundColor, "-alpha", "remove", "-alpha", "off"]);
            if (format.HasQuality) arguments.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture)]);
            if (format.Id == "png" && options.Parameters.TryGetValue("pngCompression", out var pngCompression))
            {
                if (!int.TryParse(pngCompression, out var level) || level is < 0 or > 9)
                    throw new ImageUtilityException("ImageUtility.Error.Parameter", "pngCompression");
                arguments.AddRange(["-define", "png:compression-level=" + level]);
            }
            if (format.Id == "tiff" && options.Parameters.TryGetValue("tiffCompression", out var tiffCompression))
            {
                if (tiffCompression.ToLowerInvariant() is not ("none" or "lzw" or "zip" or "jpeg" or "zstd" or "webp"))
                    throw new ImageUtilityException("ImageUtility.Error.Parameter", "tiffCompression");
                arguments.AddRange(["-compress", tiffCompression]);
            }
            if (options.Parameters.TryGetValue("lossless", out var lossless) && bool.TryParse(lossless, out var keep) && keep)
            {
                if (format.Id == "webp") arguments.AddRange(["-define", "webp:lossless=true"]);
                if (format.Id == "jxl") arguments.AddRange(["-quality", "100"]);
                if (format.Id == "avif") arguments.AddRange(["-define", "heic:lossless=true"]);
            }
            var withoutMetadata = new List<string>(arguments);
            if (!options.FormatOnly && method.IsAi) await AddSourceMetadataAsync(arguments, source, scratch, token);
            var operation = options.FormatOnly ? "conversion" : $"resize/{options.MethodId}";
            arguments.AddRange(["-set", "comment", $"%[comment]\nLOPATA: {operation}; {info.Width}x{info.Height} -> {size.Width}x{size.Height}; {DateTimeOffset.Now:O}; item={item.Id}",
                "-set", "exif:PixelXDimension", size.Width.ToString(CultureInfo.InvariantCulture),
                "-set", "exif:PixelYDimension", size.Height.ToString(CultureInfo.InvariantCulture)]);
            if (info.Frames > 1) arguments.AddRange(["-layers", "Optimize"]);
            arguments.Add(format.Id + ":" + temporaryOutput);
            try { await RunAsync(arguments, token); }
            catch (ImageUtilityException)
            {
                // Some encoders reject otherwise readable profiles. Metadata is best effort;
                // retry encoding the already processed image without optional metadata changes.
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
                if (info.Frames > 1) withoutMetadata.AddRange(["-layers", "Optimize"]);
                withoutMetadata.Add(format.Id + ":" + temporaryOutput);
                await RunAsync(withoutMetadata, token);
            }
            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
                throw new ImageUtilityException("ImageUtility.Error.NoResult", retryable: true);
            var resultInfo = await InspectControlledPathAsync(format.Id + ":" + temporaryOutput, token);
            if (resultInfo.Width != size.Width || resultInfo.Height != size.Height
                || (resultInfo.Frames != info.Frames && format.Id is not ("psd" or "psb" or "fits")))
                throw new ImageUtilityException("ImageUtility.Error.ResultDimensions", retryable: true);
            token.ThrowIfCancellationRequested();
            var desiredName = SafeFileName(ImageUtilityNaming.CreateStem(item, options));
            await using (var resultStream = File.OpenRead(temporaryOutput))
                item.PreparedOutputSha256 = Convert.ToHexString(await SHA256.HashDataAsync(resultStream, token));
            var output = CommitWithoutOverwrite(temporaryOutput, folder, desiredName, format.Extension, item, checkpoint, options.NamingMode is "series" or "date");
            item.OutputPath = output;
            return output;
        }
        finally
        {
            if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
            // Both paths were constructed from this invocation's private GUID, never from a source name.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private async Task<string> PrepareSourceAsync(ImageUtilityItem item, string scratch,
        IProgress<ImageUtilityProgress>? progress, CancellationToken token)
    {
        var source = Path.Combine(scratch, "source" + SafeInputExtension(item.DisplayName));
        if (!item.IsUrl)
        {
            if (!File.Exists(item.Source)) throw new ImageUtilityException("ImageUtility.Error.SourceMissing");
            await CopyAsync(item.Source, source, token);
            item.LocalPath = Path.GetFullPath(item.Source);
            return source;
        }
        progress?.Report(new("ImageUtility.Event.Downloading", [item.DisplayName], ItemId: item.Id));
        using var response = await Http.GetAsync(item.Source, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await using var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await stream.CopyToAsync(file, token);
        return source;
    }

    private async Task AddSourceMetadataAsync(List<string> arguments, string source, string scratch, CancellationToken token)
    {
        foreach (var profile in new[] { "icc", "exif", "xmp", "iptc" })
        {
            var file = Path.Combine(scratch, "source." + profile);
            try
            {
                await RunAsync([source, profile + ":" + file], token);
                if (File.Exists(file) && new FileInfo(file).Length > 0) arguments.AddRange(["-profile", file]);
            }
            catch (ImageUtilityException) { /* A missing/unsupported profile does not fail the image. */ }
        }
        foreach (var property in new[] { "Author", "Artist", "Copyright", "Title", "Description", "comment" })
        {
            try
            {
                var value = await RunAsync(["identify", "-format", "%[" + property + "]", source], token);
                if (!string.IsNullOrWhiteSpace(value)) arguments.AddRange(["-set", property, value.Replace("%", "%%", StringComparison.Ordinal)]);
            }
            catch (ImageUtilityException) { }
        }
    }

    public static string ResolveOutputFolder(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var folder = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!folder.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !folder.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ImageUtilityException("ImageUtility.Error.OutputFolder");
        return folder;
    }

    public static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(c => invalid.Contains(c) || c is '%' or '[' or ']' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (result.Length == 0) result = "image";
        if (ReservedNamePattern().IsMatch(result)) result = "_" + result;
        return result.Length <= 120 ? result : result[..120];
    }

    private static string CommitWithoutOverwrite(string temporary, string folder, string name, string extension,
        ImageUtilityItem item, Action? checkpoint, bool numbered = false)
    {
        for (var index = 0; ; index++)
        {
            var suffix = numbered ? "_" + (index + 1).ToString("D4", CultureInfo.InvariantCulture)
                : index == 0 ? "" : "_" + index.ToString("D4", CultureInfo.InvariantCulture);
            var destination = Path.Combine(folder, name + suffix + "." + extension);
            if (File.Exists(destination)) continue;
            item.PlannedOutputPath = destination;
            checkpoint?.Invoke();
            try { File.Move(temporary, destination, overwrite: false); return destination; }
            catch (IOException) when (File.Exists(destination)) { }
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
    }

    private static string SafeInputExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return ImageUtilitySources.SupportedExtensions.Contains(extension) ? extension : ".img";
    }

    private static bool IsAnimatedPng(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        while (stream.Position + 12 <= stream.Length)
        {
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > stream.Length - stream.Position - 4) return false;
            if (header[4..8].SequenceEqual("acTL"u8))
            {
                if (length < 8) return false;
                stream.ReadExactly(header);
                return BinaryPrimitives.ReadUInt32BigEndian(header[..4]) > 1;
            }
            stream.Seek(length + 4L, SeekOrigin.Current);
        }
        return false;
    }

    private static void ValidateOptions(ImageUtilityOptions options)
    {
        if (options.Quality is < 1 or > 100 || !double.IsFinite(options.Sharpen) || options.Sharpen is < 0 or > 3)
            throw new ImageUtilityException("ImageUtility.Error.Parameter");
        if (!ColorPattern().IsMatch(options.BackgroundColor)) throw new ImageUtilityException("ImageUtility.Error.Color");
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    [GeneratedRegex(@"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedNamePattern();
}
