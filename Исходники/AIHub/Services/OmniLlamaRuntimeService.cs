using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed class OmniLlamaRuntimeService(ManagedModelLibraryStore library, OmniLlamaProfile? profile = null) : IOmniTextRuntime
{
    private readonly OmniLlamaProfile _profile = profile ?? OmniLlamaProfile.Alpha;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ImageAnalysisHeavyResourcePlanningService _resources = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private int _port;
    private bool _ready;
    private bool _disposed;
    private OmniWarmupResult? _warmup;
    public string BundleId => _profile.BundleId;
    public string PipelineId => _profile.PipelineId;
    public string PipelineVersion => _profile.PipelineVersion;
    public string ModelId => _profile.Repository;
    public string ModelRevision => _profile.Revision;
    public string RuntimeId => ImageAnalysisRuntimeIds.Qwen35Llama;
    public string RuntimeVersion => LlamaBackendPaths.Release;
    public string DeviceMapJson => OmniLlamaProtocol.DescribeDeviceMap(_profile);
    public bool IsReady
    {
        get
        {
            try { return _ready && _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public ImageAnalysisHeavyResourcePlan? CurrentPlan { get; private set; }

    public async Task<OmniWarmupResult> PrepareAsync(Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken cancellationToken, bool reuseCurrentPlan = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ComponentLicenseGate.EnsureAsync(_profile.ArtifactId, cancellationToken);
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady && _warmup is not null) return _warmup with { AlreadyLoaded = true };
            Stop();
            var card = library.Load(_profile.ArtifactId)
                ?? throw new InvalidDataException("The Omni model is not registered. Download it using the model button.");
            if (card.Status != ManagedModelStatuses.Installed || card.Revision != ModelRevision)
                throw new InvalidDataException("Download and verify the complete Omni model and projector first.");
            if (!File.Exists(LlamaBackendPaths.ServerExecutablePath))
                throw new FileNotFoundException("The managed llama.cpp backend is missing.", LlamaBackendPaths.ServerExecutablePath);
            var model = ResolveFile(card, "main_model");
            var projector = ResolveFile(card, "projector");
            var before = await _resources.CaptureCurrentAsync(cancellationToken);
            CurrentPlan = new([before], before.AvailableRamBytes, before.AvailableVramBytes,
                before.CommitAvailableBytes, 0, 0, 0, 0, true, _profile.Label + "_gpu_model_and_projector_fixed_no_fit");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
            {
                WorkingDirectory = LlamaBackendPaths.DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in OmniLlamaProtocol.Arguments(model, projector, _port)) info.ArgumentList.Add(arg);
            // Inherited llama settings must not enable downloads, change the model, or alter the tested profile.
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)).ToArray())
                info.Environment.Remove(key);
            lock (_stderr) _stderr.Clear();
            var timer = Stopwatch.StartNew();
            var process = new Process { StartInfo = info };
            _process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) RetainLine(line, log); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) RetainLine(line, log); };
            if (!process.Start()) throw new InvalidOperationException("Omni llama-server failed to start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            log(RuntimeResourceDiagnostics.DescribeLaunch(_profile.Label, process, DeviceMapJson, model));
            progress?.Report(new(ManagedModelRoles.Vision, "runtime_loading", "Preparing Omni model and projector."));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"Omni runtime exited ({process.ExitCode}): {ErrorTail()}");
                try
                {
                    using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/health", timeout.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(300, timeout.Token);
            }
            var after = await _resources.CaptureCurrentAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_process, process)) throw new OperationCanceledException("Omni startup was stopped.");
            _ready = true;
            _warmup = new(false, timer.ElapsedMilliseconds, CurrentPlan, RuntimeVersion, DeviceMapJson,
                RuntimeResourceDiagnostics.Capture(process).PeakWorkingSetBytes,
                before.AvailableRamBytes, after.AvailableRamBytes, before.CommitAvailableBytes, after.CommitAvailableBytes,
                before.AvailableVramBytes, after.AvailableVramBytes);
            log(RuntimeResourceDiagnostics.DescribeSnapshot(_profile.Label, process, "loaded"));
            return _warmup;
        }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<OmniTextGenerationResult> GenerateAsync(string command, string imagePath,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken cancellationToken, Action<string>? responseReceived = null, Action<string>? diagnosticReceived = null)
    {
        if (command is not ("analyze" or "compose" or "revise")) throw new ArgumentOutOfRangeException(nameof(command));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsReady) throw new InvalidOperationException("The Omni runtime is not ready.");
            diagnosticReceived?.Invoke(RuntimeResourceDiagnostics.DescribeSnapshot(_profile.Label, _process!, command + "_before"));
            // WPF decodes every accepted source format into PNG at its original pixel size, without resizing.
            var hasImage = conversation.Any(m => m.IncludesImage);
            var dataUrl = hasImage ? await Task.Run(() => LoadImageDataUrl(imagePath), cancellationToken) : string.Empty;
            var inputBudget = await OmniLlamaContextProbe.MeasureAsync(_http, new Uri($"http://127.0.0.1:{_port}/"),
                conversation, dataUrl, cancellationToken, _profile, command);
            diagnosticReceived?.Invoke($"Omni context admission: stage={command}; inputUpperBound={inputBudget}; reserve={OmniContextBudget.ResponseReserveTokens}; context={OmniLlamaProtocol.ContextTokens}.");
            var outputBudget = OmniContextBudget.OutputBudget(inputBudget, OmniLlamaProtocol.ContextTokens);
            if (!hasImage && (long)inputBudget * 3 > (long)OmniContextBudget.Boundary(OmniLlamaProtocol.ContextTokens) * 2)
                throw new ImageAnalysisContextExhaustedException("Text batch input leaves less than half its size for output.");
            var request = OmniLlamaProtocol.BuildRequest(conversation, dataUrl, outputBudget, _profile, command);
            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/v1/chat/completions")
            { Content = new StringContent(request, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                responseReceived?.Invoke(error);
                if (error.Contains("context", StringComparison.OrdinalIgnoreCase)) throw new ImageAnalysisContextExhaustedException(error);
                throw new InvalidOperationException($"Omni HTTP {(int)response.StatusCode}: {error}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await OmniLlamaProtocol.ReadAsync(stream, streamProgress, responseReceived, cancellationToken, _profile);
        }
        catch (OperationCanceledException) { Stop(); throw; }
        finally
        {
            try
            {
                if (_process is { HasExited: false } process)
                    diagnosticReceived?.Invoke(RuntimeResourceDiagnostics.DescribeSnapshot(_profile.Label, process, command + "_after"));
            }
            catch (InvalidOperationException) { } // The UI may stop and dispose this owned process concurrently.
            _gate.Release();
        }
    }

    private static string LoadImageDataUrl(string path)
    {
        using var file = File.OpenRead(path);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(file,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public async Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(CancellationToken cancellationToken)
    {
        var sample = await _resources.CaptureCurrentAsync(cancellationToken);
        return new(sample, sample.AvailableRamBytes < 512L * 1024 * 1024,
            sample.CommitAvailableBytes < 512L * 1024 * 1024,
            sample.TotalVramBytes > 0 && sample.AvailableVramBytes < 256L * 1024 * 1024, false);
    }

    private void RetainLine(string line, Action<string> log)
    {
        lock (_stderr) { _stderr.Enqueue(line); while (_stderr.Count > 30) _stderr.Dequeue(); }
        log(_profile.Label + " backend: " + line);
    }
    private string ErrorTail() { lock (_stderr) return string.Join(Environment.NewLine, _stderr); }
    private static string ResolveFile(ManagedModelArtifactCard card, string purpose)
    {
        var file = card.Files.Single(f => f.Purpose == purpose);
        var root = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length != file.SizeBytes)
            throw new InvalidDataException("A verified Omni file is missing or changed.");
        return path;
    }
    public void Stop()
    {
        _ready = false;
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); _http.Dispose(); }
}
