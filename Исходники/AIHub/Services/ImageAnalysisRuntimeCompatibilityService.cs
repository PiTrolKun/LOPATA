using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageAnalysisRuntimeCompatibilityService
{
    internal const string FlorenceErrorMarker = "AI_HUB_SMOKE_ERROR:";
    private static readonly TimeSpan KimiStartupTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan FlorenceTimeout = TimeSpan.FromMinutes(8);
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private readonly ManagedModelLibraryStore _store;

    public ImageAnalysisRuntimeCompatibilityService(ManagedModelLibraryStore store)
    {
        _store = store;
    }

    public async Task<ManagedModelArtifactCard> VerifyAsync(
        string modelArtifactId,
        CancellationToken cancellationToken,
        IProgress<ManagedModelDownloadProgress>? progress = null)
    {
        var card = _store.Load(modelArtifactId)
            ?? throw new InvalidOperationException("The model card is not registered in the LOPATA library.");
        try
        {
            if (string.Equals(card.ModelArtifactId, ManagedModelCatalog.KimiMediumArtifactId, StringComparison.Ordinal))
            {
                ReportRuntimeProgress(card, progress, "runtime_loading");
                await VerifyKimiAsync(card, cancellationToken);
            }
            else if (string.Equals(card.ModelArtifactId, ManagedModelCatalog.FlorenceLargeArtifactId, StringComparison.Ordinal))
            {
                ReportRuntimeProgress(card, progress, "runtime_loading");
                await VerifyFlorenceAsync(card, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("No runtime smoke-check is registered for this model.");
            }

            card.Status = ManagedModelStatuses.Installed;
            card.RuntimeVerifiedAt = DateTimeOffset.Now;
            card.LastError = string.Empty;
            ReportRuntimeProgress(card, progress, "runtime_verified");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            card.Status = ManagedModelStatuses.RuntimeIncompatible;
            card.RuntimeVerifiedAt = null;
            card.LastError = CreateSafeError(ex);
            _store.Upsert(card);
            throw new InvalidOperationException(card.LastError, ex);
        }

        return _store.Upsert(card);
    }

    private static async Task VerifyKimiAsync(
        ManagedModelArtifactCard card,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ChatLlmBackendPaths.ServerExecutablePath))
        {
            throw new FileNotFoundException("The verified chatllm.cpp backend is not installed.");
        }
        if (!File.Exists(ChatLlmBackendPaths.ImageMagickExecutablePath))
        {
            throw new FileNotFoundException("The private ImageMagick runtime for chatllm.cpp is not installed.");
        }
        var modelPath = ResolveFile(card, "main_model");
        var port = ReserveLoopbackPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = ChatLlmBackendPaths.ServerExecutablePath,
            WorkingDirectory = ChatLlmBackendPaths.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var existingPath = startInfo.Environment.TryGetValue("PATH", out var inheritedPath)
            ? inheritedPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = ChatLlmBackendPaths.ImageMagickDirectoryPath
            + Path.PathSeparator
            + existingPath;
        startInfo.Environment["MAGICK_HOME"] = ChatLlmBackendPaths.ImageMagickDirectoryPath;
        foreach (var argument in ImageAnalysisKimiRequestBuilder.BuildArguments(modelPath, port))
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = OwnedProcessRegistry.Shared.Start(startInfo, "ImageAnalysisRuntimeCompatibilityService")
            ?? throw new InvalidOperationException("The local Kimi runtime could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await WaitForServerAsync(client, process, port, cancellationToken);
            var payload = ImageAnalysisKimiRequestBuilder.BuildRequestBody(
                $"data:image/png;base64,{OnePixelPng}",
                "Describe what you see in this image.");
            using var response = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/chat/completions",
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            _ = ImageAnalysisKimiRequestBuilder.ParseResponseContent(
                await response.Content.ReadAsStringAsync(cancellationToken));
        }
        finally
        {
            TryStop(process);
            _ = await outputTask;
            _ = await errorTask;
        }
    }

    private static async Task VerifyFlorenceAsync(
        ManagedModelArtifactCard card,
        CancellationToken cancellationToken)
    {
        var pythonPath = FindUpward("Runtime", "Python", "reranker", ".venv", "Scripts", "python.exe")
            ?? throw new FileNotFoundException("The managed Python/Transformers runtime was not found.");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "florence_smoke_check.py");
        if (!File.Exists(scriptPath))
        {
            scriptPath = FindUpward("Исходники", "AIHub", "Tools", "florence_smoke_check.py")
                ?? throw new FileNotFoundException("The Florence runtime smoke-check is missing.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" \"{card.InstallDirectory}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["HF_DATASETS_OFFLINE"] = "1";
        using var process = OwnedProcessRegistry.Shared.Start(startInfo, "ImageAnalysisRuntimeCompatibilityService")
            ?? throw new InvalidOperationException("The local Florence runtime could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FlorenceTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            TryStop(process);
            throw;
        }
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0 || !string.Equals(output, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Florence did not pass the local offline smoke-check."
                : ExtractFlorenceError(error));
        }
    }

    internal static string ExtractFlorenceError(string standardError)
    {
        var markerIndex = standardError.LastIndexOf(
            FlorenceErrorMarker,
            StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var markedError = standardError[(markerIndex + FlorenceErrorMarker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(markedError))
            {
                return markedError;
            }
        }
        var lines = standardError.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault()
            ?? "Florence did not pass the local offline smoke-check.";
    }

    private static void ReportRuntimeProgress(
        ManagedModelArtifactCard card,
        IProgress<ManagedModelDownloadProgress>? progress,
        string stage) => progress?.Report(new ManagedModelDownloadProgress(
            card.ModelArtifactId,
            card.DisplayName,
            stage == "runtime_verified" ? 1 : 0,
            stage == "runtime_verified" ? 1 : 0,
            0,
            stage));

    private static async Task WaitForServerAsync(
        HttpClient client,
        Process process,
        int port,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + KimiStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("The Kimi runtime stopped during startup.");
            }
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health", cancellationToken);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.IsSuccessStatusCode && !body.Contains("loading model", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(700, cancellationToken);
        }
        throw new TimeoutException("The Kimi runtime did not become ready in time.");
    }

    private static string ResolveFile(ManagedModelArtifactCard card, string purpose)
    {
        var file = card.Files.FirstOrDefault(item => string.Equals(item.Purpose, purpose, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The model manifest has no required '{purpose}' file.");
        var root = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("A verified model file is missing.", path);
        }
        return path;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryStop(Process process)
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
        }
    }

    private static string? FindUpward(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = parts.Aggregate(directory.FullName, Path.Combine);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static string CreateSafeError(Exception error)
    {
        var message = error is OperationCanceledException
            ? "The runtime check was cancelled."
            : error.Message;
        return message.Length <= 500 ? message : message[..500];
    }
}
