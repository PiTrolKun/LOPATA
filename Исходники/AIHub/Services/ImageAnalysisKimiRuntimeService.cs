using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageAnalysisKimiRuntimeService : IDisposable
{
    private readonly ManagedModelLibraryStore _libraryStore;
    private readonly VisionImagePayloadService _imagePayloadService = new();
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly object _processLock = new();

    private Process? _process;
    private string? _currentModelPath;
    private int _port;
    private bool _disposed;

    public ImageAnalysisKimiRuntimeService(ManagedModelLibraryStore libraryStore)
    {
        _libraryStore = libraryStore;
    }

    public async Task PrepareAsync(
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var modelPath = ResolveReadyModelPath();
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Vision,
            "starting_cpu",
            "Starting Kimi with the verified CPU/RAM profile."));
        await EnsureStartedAsync(modelPath, log, cancellationToken);
    }

    public async Task<string> DescribeAsync(
        ImageAnalysisFilePassport passport,
        string prompt,
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ThrowIfDisposed();
        var modelPath = ResolveReadyModelPath();

        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Vision,
            "preparing_image",
            "Preparing the selected image for Kimi."));
        var payload = await _imagePayloadService.PrepareAsync(
            new SessionFileReference
            {
                Id = "image-analysis-source",
                SourcePath = passport.SourcePath,
                DisplayName = passport.DisplayName,
                Extension = passport.Extension,
                Category = SessionFileCategories.Image,
                SizeBytes = passport.SizeBytes,
                IsAvailable = true
            },
            cancellationToken);
        var dataUri = $"data:{payload.MimeType};base64,{Convert.ToBase64String(payload.Bytes)}";
        var requestJson = ImageAnalysisKimiRequestBuilder.BuildRequestBody(dataUri, prompt);

        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Vision,
            "starting_cpu",
            "Starting Kimi with the verified CPU/RAM profile."));
        var port = await EnsureStartedAsync(modelPath, log, cancellationToken);
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Vision,
            "analysing",
            "Kimi is reading the image and preparing a grounded report."));
        string responseBody;
        try
        {
            responseBody = await SendRequestAsync(port, requestJson, cancellationToken);
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested
            && (ex.StatusCode is null || (int)ex.StatusCode >= 500))
        {
            log($"The visual analyst connection was interrupted ({ex.Message}). Restarting and retrying once.");
            progress?.Report(new ImageAnalysisLiteraryProgress(
                ManagedModelRoles.Vision,
                "restarting",
                "The Kimi connection was interrupted. Restarting it and retrying once."));
            Stop();
            port = await EnsureStartedAsync(modelPath, log, cancellationToken);
            responseBody = await SendRequestAsync(port, requestJson, cancellationToken);
        }
        return ImageAnalysisKimiRequestBuilder.ParseResponseContent(responseBody);
    }

    public void Stop()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
            _currentModelPath = null;
            _port = 0;
        }

        if (process is null)
        {
            return;
        }

        StopServer(process);
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Stop();
        _httpClient.Dispose();
        _disposed = true;
    }

    private async Task<int> EnsureStartedAsync(
        string modelPath,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync(ManagedModelCatalog.KimiMediumArtifactId, cancellationToken);
        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_processLock)
            {
                if (_process is not null
                    && !_process.HasExited
                    && string.Equals(_currentModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    return _port;
                }
            }

            Stop();
            var port = ReserveLoopbackPort();
            var process = StartServer(modelPath, port, log);
            lock (_processLock)
            {
                _process = process;
                _currentModelPath = modelPath;
                _port = port;
            }

            try
            {
                await WaitForServerAsync(process, port, cancellationToken);
                log(RuntimeResourceDiagnostics.DescribeSnapshot(
                    "Kimi visual analyst / chatllm.cpp",
                    process,
                    "ready"));
                return port;
            }
            catch
            {
                ClearAndStop(process);
                throw;
            }
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task<string> SendRequestAsync(
        int port,
        string requestJson,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"http://127.0.0.1:{port}/v1/chat/completions",
            content,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return responseBody;
    }

    private static Process StartServer(
        string modelPath,
        int port,
        Action<string> log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ChatLlmBackendPaths.ServerExecutablePath,
            WorkingDirectory = ChatLlmBackendPaths.DirectoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var existingPath = startInfo.Environment.TryGetValue("PATH", out var inheritedPath)
            ? inheritedPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = ChatLlmBackendPaths.ImageMagickDirectoryPath
            + Path.PathSeparator
            + existingPath;
        startInfo.Environment["MAGICK_HOME"] = ChatLlmBackendPaths.ImageMagickDirectoryPath;
        foreach (var argument in ImageAnalysisKimiRequestBuilder.BuildArguments(
            modelPath,
            port))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => LogBackendLine(args.Data, log);
        process.ErrorDataReceived += (_, args) => LogBackendLine(args.Data, log);
        OwnedProcessRegistry.Shared.Start(process, "ImageAnalysisKimiRuntimeService");
        log(RuntimeResourceDiagnostics.DescribeLaunch(
            "Kimi visual analyst / chatllm.cpp",
            process,
            "chatllm.cpp automatic placement; CPU/RAM and CUDA VRAM/shared GPU memory may be used",
            modelPath));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WaitForServerAsync(
        Process process,
        int port,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The visual analyst runtime exited with code {process.ExitCode}.");
            }
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"http://127.0.0.1:{port}/health",
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The model is still loading.
            }
            await Task.Delay(750, cancellationToken);
        }
        throw new TimeoutException("The visual analyst did not become ready in time.");
    }

    private string ResolveReadyModelPath()
    {
        var card = _libraryStore.Load(ManagedModelCatalog.KimiMediumArtifactId)
            ?? throw new InvalidOperationException("The visual analyst is not registered in the LOPATA model library.");
        if (card.Status != ManagedModelStatuses.Installed)
        {
            throw new InvalidOperationException("The visual analyst is not verified and ready for image analysis.");
        }

        if (!File.Exists(ChatLlmBackendPaths.ServerExecutablePath))
        {
            throw new FileNotFoundException(
                "The verified runtime required for the visual analyst was not found.",
                ChatLlmBackendPaths.ServerExecutablePath);
        }
        if (!File.Exists(ChatLlmBackendPaths.ImageMagickExecutablePath))
        {
            throw new FileNotFoundException(
                "The private image decoder required by the visual analyst was not found.",
                ChatLlmBackendPaths.ImageMagickExecutablePath);
        }

        return ResolveFile(card, "main_model");
    }

    private void ClearAndStop(Process process)
    {
        lock (_processLock)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _currentModelPath = null;
                _port = 0;
            }
        }

        StopServer(process);
        process.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string ResolveFile(ManagedModelArtifactCard card, string purpose)
    {
        var file = card.Files.FirstOrDefault(item => string.Equals(
            item.Purpose,
            purpose,
            StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The visual analyst manifest has no '{purpose}' file.");
        var root = Path.GetFullPath(card.InstallDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new FileNotFoundException("A verified visual analyst file is missing.", path);
        }
        return path;
    }

    private static int ReserveLoopbackPort()
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
            // Best-effort local runtime shutdown.
        }
    }

    private static void LogBackendLine(string? line, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        if (line.Contains("serving at", StringComparison.OrdinalIgnoreCase)
            || line.Contains("POST /v1/chat/completions", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || line.Contains("gpu", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vram", StringComparison.OrdinalIgnoreCase)
            || line.Contains("offload", StringComparison.OrdinalIgnoreCase)
            || line.Contains("memory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            log(line.Trim());
        }
    }
}
