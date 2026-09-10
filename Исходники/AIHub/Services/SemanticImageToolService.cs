using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class SemanticImageToolService
{
    internal const string ModelComponentId = "model.vision.smolvlm2.q4km";
    internal const string ProjectorComponentId = "model.vision.smolvlm2.projector";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly VisionImagePayloadService _imagePayloadService = new();

    public async Task<string> DescribeAsync(
        SessionFileManifest manifest,
        string fileId,
        string prompt,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync(ModelComponentId, cancellationToken);
        var image = ResolveImage(manifest, fileId);
        var statuses = new ComponentManager().GetStatus(ComponentKinds.Processing)
            .ToDictionary(status => status.Entry.Id, StringComparer.OrdinalIgnoreCase);
        var modelPath = ResolveComponentArtifact(statuses, ModelComponentId);
        var projectorPath = ResolveComponentArtifact(statuses, ProjectorComponentId);
        var serverPath = LlamaBackendPaths.ServerExecutablePath;
        if (!File.Exists(serverPath))
        {
            throw new SessionFileToolException(
                "vision_runtime_missing",
                "The verified llama.cpp runtime required for semantic image analysis is unavailable.");
        }

        var imagePayload = await _imagePayloadService.PrepareAsync(image, cancellationToken);
        var dataUri = $"data:{imagePayload.MimeType};base64,{Convert.ToBase64String(imagePayload.Bytes)}";
        var requestJson = BuildRequestBody(dataUri, prompt, languageCode);

        var failures = new List<VisionRuntimeAttemptException>();
        foreach (var gpuLayers in new[] { 99, 0 })
        {
            try
            {
                var description = await RunInferenceAsync(
                    serverPath,
                    modelPath,
                    projectorPath,
                    requestJson,
                    gpuLayers,
                    cancellationToken);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    evidence_type = "semantic_vision",
                    model = ComponentCatalog.Find(ModelComponentId)?.Name ?? ModelComponentId,
                    source_file_id = image.Id,
                    source_format = imagePayload.SourceExtension,
                    vision_input_format = imagePayload.MimeType,
                    vision_input_normalized = imagePayload.WasNormalized,
                    description
                }, JsonOptions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (VisionRuntimeAttemptException ex)
            {
                failures.Add(ex);
            }
        }

        throw CreateInferenceFailure(failures);
    }

    internal static SessionFileToolException CreateInferenceFailure(
        IReadOnlyCollection<VisionRuntimeAttemptException> failures) =>
        new(
            "semantic_vision_failed",
            failures.Count == 0
                ? "The local semantic vision model could not produce a grounded description."
                : "The local semantic vision model failed with both GPU and CPU fallback modes. Runtime diagnostics were recorded locally.",
            string.Join(Environment.NewLine, failures.Select(failure =>
                failure.CreateDiagnosticSummary())));

    internal static IReadOnlyList<string> BuildArguments(
        string modelPath,
        string projectorPath,
        int port,
        int gpuLayers) =>
    [
        "-m", modelPath,
        "--mmproj", projectorPath,
        "--host", "127.0.0.1",
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--ctx-size", "4096",
        "--n-gpu-layers", gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--jinja",
        "--reasoning", "off",
        "--no-webui"
    ];

    internal static string BuildRequestBody(
        string imageDataUri,
        string prompt,
        string languageCode,
        int maxTokens = 900)
    {
        var normalizedLanguage = languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? "Russian (ru)"
            : languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "English (en)"
                : languageCode.Trim();
        var focusedPrompt = string.IsNullOrWhiteSpace(prompt)
            ? languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                ? "Фактически и достаточно подробно опиши видимое содержимое изображения."
                : "Describe the visible content of this image factually and in useful detail."
            : prompt.Trim();
        return JsonSerializer.Serialize(new
        {
            model = "local-vision",
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a local visual evidence adapter. Use only the supplied image. Describe visible objects, people, text, composition and relevant details. Never invent identity, context or hidden details. State uncertainty explicitly. "
                        + $"Answer strictly in {normalizedLanguage}; do not switch languages even if the image or focused question contains another language."
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = focusedPrompt },
                        new { type = "image_url", image_url = new { url = imageDataUri } }
                    }
                }
            },
            temperature = 0.1,
            max_tokens = Math.Clamp(maxTokens, 256, 4096),
            stream = false
        }, JsonOptions);
    }

    private static SessionFileReference ResolveImage(SessionFileManifest manifest, string fileId)
    {
        var reference = manifest.Files.FirstOrDefault(file =>
            string.Equals(file.Id, fileId, StringComparison.Ordinal));
        if (reference is null)
        {
            throw new SessionFileToolException(
                "file_not_found",
                "The requested image is not attached to this session.");
        }

        if (!reference.IsAvailable || !File.Exists(reference.SourcePath))
        {
            throw new SessionFileToolException(
                "file_unavailable",
                "The requested image is no longer available at its recorded location.");
        }

        if (!string.Equals(reference.Category, SessionFileCategories.Image, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionFileToolException(
                "unsupported_file_category",
                "Semantic image analysis accepts only an attached image file.");
        }

        return reference;
    }

    private static string ResolveComponentArtifact(
        IReadOnlyDictionary<string, ComponentStatusSnapshot> statuses,
        string componentId)
    {
        if (!statuses.TryGetValue(componentId, out var status) || !status.IsAvailable)
        {
            throw new SessionFileToolException(
                "semantic_vision_component_missing",
                "The verified local semantic vision component is not installed.");
        }

        var expectedName = string.IsNullOrWhiteSpace(status.Entry.HealthCheckRelativePath)
            ? status.Entry.FileName
            : status.Entry.HealthCheckRelativePath;
        var directPath = Path.Combine(status.Record.InstallPath, expectedName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        var discovered = Directory.Exists(status.Record.InstallPath)
            ? Directory.EnumerateFiles(
                    status.Record.InstallPath,
                    Path.GetFileName(expectedName),
                    SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            return discovered;
        }

        throw new SessionFileToolException(
            "semantic_vision_component_invalid",
            "The semantic vision component is recorded as installed but its verified artifact is missing.");
    }

    private static async Task<string> RunInferenceAsync(
        string serverPath,
        string modelPath,
        string projectorPath,
        string requestJson,
        int gpuLayers,
        CancellationToken cancellationToken)
    {
        var port = FindFreeLoopbackPort();
        var diagnostics = new VisionRuntimeDiagnosticBuffer();
        using var process = StartServer(
            serverPath,
            modelPath,
            projectorPath,
            port,
            gpuLayers,
            diagnostics);
        try
        {
            await WaitForHealthAsync(process, port, cancellationToken);
            using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                content,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new VisionRuntimeAttemptException(
                    gpuLayers,
                    response.StatusCode,
                    responseBody,
                    diagnostics);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var description = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()
                ?.Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                throw new InvalidOperationException("The semantic vision model returned an empty response.");
            }

            return description;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VisionRuntimeAttemptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VisionRuntimeAttemptException(
                gpuLayers,
                statusCode: null,
                responseBody: string.Empty,
                diagnostics,
                ex);
        }
        finally
        {
            StopServer(process);
        }
    }

    private static Process StartServer(
        string serverPath,
        string modelPath,
        string projectorPath,
        int port,
        int gpuLayers,
        VisionRuntimeDiagnosticBuffer diagnostics)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in BuildArguments(modelPath, projectorPath, port, gpuLayers))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
            diagnostics.Add("stdout", eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
            diagnostics.Add("stderr", eventArgs.Data);
        OwnedProcessRegistry.Shared.Start(process, "SemanticImageToolService");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForHealthAsync(
        Process process,
        int port,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The semantic vision server stopped during startup with exit code {process.ExitCode}.");
            }

            try
            {
                using var response = await httpClient.GetAsync(
                    $"http://127.0.0.1:{port}/health",
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The local endpoint is not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A single health probe timed out; startup can still continue.
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("The semantic vision server did not become ready in time.");
    }

    private static void StopServer(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Runtime cleanup is best-effort.
        }
    }

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

}
