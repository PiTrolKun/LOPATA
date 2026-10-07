using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class OmniLlamaRuntimeService(ManagedModelLibraryStore library, OmniLlamaProfile? profile = null, bool forceCpu = false, HttpMessageHandler? testHandler = null) : IOmniTextRuntime
{
    private readonly OmniLlamaProfile _profile = profile ?? OmniLlamaProfile.Alpha;
    private readonly HttpClient _http = testHandler is null ? new() { Timeout = Timeout.InfiniteTimeSpan }
        : new(testHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ImageAnalysisHeavyResourcePlanningService _resources = new();
    private readonly Queue<string> _stderr = new();
    private LlamaRuntimeSelection? _selectedRuntime;
    private int _nativeHardwareFailed;
    internal string? CurrentExecutable => _selectedRuntime?.Bundle.Server;
    internal Uri Endpoint => new($"http://127.0.0.1:{_port}/");
    private readonly object _retirementGate = new();
    private Task _processRetirement = Task.CompletedTask;
    private Process? _process;
    private int _port;
    private bool _ready;
    private bool _disposed;
    private bool _backgroundRegistered;
    private OmniWarmupResult? _warmup;
    public string BundleId => _profile.BundleId;
    public string PipelineId => _profile.PipelineId;
    public string PipelineVersion => _profile.PipelineVersion;
    public string ModelId => _profile.Repository;
    public string ModelRevision => _profile.Revision;
    public string RuntimeId => ImageAnalysisRuntimeIds.Qwen35Llama;
    public string RuntimeVersion => _selectedRuntime?.Bundle.ComponentId is not null ? ComponentCatalog.Find(_selectedRuntime.Bundle.ComponentId)!.Version : LlamaBackendPaths.Release;
    public string DeviceMapJson => OmniLlamaProtocol.DescribeDeviceMap(_profile, _selectedRuntime?.Bundle.Backend ?? "CPU", _selectedRuntime?.DeviceId ?? "none");
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
        if (!_backgroundRegistered)
        {
            ApplicationBackgroundOperations.RegisterModel(this, async runtime => { runtime.Stop(); await runtime.AwaitProcessRetirementAsync(CancellationToken.None); });
            _backgroundRegistered = true;
        }
        await ComponentLicenseGate.EnsureAsync(_profile.ArtifactId, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady && _warmup is not null) return _warmup with { AlreadyLoaded = true };
            try { return await PrepareAttemptAsync(log, progress, cancellationToken, forceCpu); }
            catch (Exception error) when (!forceCpu && _selectedRuntime?.UsesGpu == true
                && Volatile.Read(ref _nativeHardwareFailed) != 0 && !cancellationToken.IsCancellationRequested
                && error is not OperationCanceledException)
            {
                Stop();
                log("Omni GPU hardware failure; one clean CPU startup retry.");
                return await PrepareAttemptAsync(log, progress, cancellationToken, true);
            }
        }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }

    private async Task<OmniWarmupResult> PrepareAttemptAsync(Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken cancellationToken, bool useCpu)
    {
            Stop();
            await AwaitProcessRetirementAsync(cancellationToken);
            var card = library.Load(_profile.ArtifactId)
                ?? throw new InvalidDataException("The Omni model is not registered. Download it using the model button.");
            if (card.Status != ManagedModelStatuses.Installed || card.Revision != ModelRevision)
                throw new InvalidDataException("Download and verify the complete Omni model and projector first.");
            var model = ResolveFile(card, "main_model");
            var projector = ResolveFile(card, "projector");
            var metadata = await Task.Run(() => LiteraryModelMemoryMetadata.Read(model), cancellationToken);
            var projectorBytes = new FileInfo(projector).Length;
            _selectedRuntime = await LlamaRuntimeSelector.SelectAsync(
                OmniRuntimeMemoryPolicy.GpuRequired(metadata, projectorBytes), useCpu, log, cancellationToken);
            if (!_selectedRuntime.UsesGpu) OmniRuntimeMemoryPolicy.EnsureCurrentCpuMemory(metadata, projectorBytes);
            Volatile.Write(ref _nativeHardwareFailed, 0);
            var before = await _resources.CaptureForRuntimeAsync(_selectedRuntime, cancellationToken);
            CurrentPlan = new([before], before.AvailableRamBytes, before.AvailableVramBytes,
                before.CommitAvailableBytes, before.AvailableRamBytes, before.AvailableVramBytes,
                Math.Max(4 * LlamaDenseMemoryPolicy.GiB, before.TotalRamBytes / 10),
                _selectedRuntime.UsesGpu ? LlamaDenseMemoryPolicy.GiB : 0,
                _selectedRuntime.UsesGpu, _profile.Label + "_" + _selectedRuntime.Bundle.Backend + "_full_context_no_fit");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var info = new ProcessStartInfo(_selectedRuntime.Bundle.Server)
            {
                WorkingDirectory = _selectedRuntime.Bundle.Directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var arg in OmniLlamaProtocol.Arguments(model, projector, _port, _selectedRuntime.DeviceId, _selectedRuntime.UsesGpu)) info.ArgumentList.Add(arg);
            RuntimeDeviceProbe.ClearBackendOverrides(info);
            lock (_stderr) _stderr.Clear();
            var timer = Stopwatch.StartNew();
            var process = new Process { StartInfo = info };
            _process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) RetainLine(line, log); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) RetainLine(line, log); };
            if (!OwnedProcessRegistry.Shared.Start(process, "OmniLlamaRuntimeService")) throw new InvalidOperationException("Omni llama-server failed to start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            log(RuntimeResourceDiagnostics.DescribeLaunch(_profile.Label, process, DeviceMapJson, model));
            progress?.Report(new(ManagedModelRoles.Vision, "runtime_loading", "Preparing Omni model and projector."));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(_selectedRuntime.UsesGpu ? 8 : 15));
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
            var after = await _resources.CaptureForRuntimeAsync(_selectedRuntime, cancellationToken);
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
            return await CompleteRequestAsync(request, streamProgress, responseReceived, diagnosticReceived, cancellationToken);
        }
        catch (OperationCanceledException) { Stop(); await AwaitProcessRetirementAsync(CancellationToken.None); throw; }
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
        var selection = _selectedRuntime ?? throw new InvalidOperationException("Omni runtime has not selected a device.");
        var sample = await _resources.CaptureForRuntimeAsync(selection, cancellationToken);
        return new(sample, sample.AvailableRamBytes < 512L * 1024 * 1024,
            sample.CommitAvailableBytes < 512L * 1024 * 1024,
            sample.TotalVramBytes > 0 && sample.AvailableVramBytes < 256L * 1024 * 1024, false);
    }

    private void RetainLine(string line, Action<string> log)
    {
        if (NativeHardwareFailure.IsRecoverable(line)) Interlocked.Exchange(ref _nativeHardwareFailed, 1);
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
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch { Interlocked.CompareExchange(ref _process, process, null); throw; }
        lock (_retirementGate) _processRetirement = Task.WhenAll(_processRetirement, RetireProcessAsync(process));
    }
    internal async Task AwaitProcessRetirementAsync(CancellationToken token)
    {
        Task pending;
        lock (_retirementGate) pending = _processRetirement;
        await pending.WaitAsync(TimeSpan.FromSeconds(20), token);
    }
    private static async Task RetireProcessAsync(Process process)
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); _http.Dispose(); }
}
