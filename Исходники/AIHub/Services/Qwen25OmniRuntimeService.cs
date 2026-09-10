using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class Qwen25OmniRuntimeService : IOmniTextRuntime
{
    internal static Encoding ProtocolEncoding { get; } = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ManagedModelLibraryStore _libraryStore;
    private readonly ImageAnalysisHeavyResourcePlanningService _resourcePlanning = new();
    private ImageAnalysisHeavyResourceSample? _postWarmupSample;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processSync = new();
    private readonly Queue<string> _standardErrorTail = new();
    private Process? _process;
    private Task? _errorDrainTask;
    private int _nextRequestId;
    private bool _disposed;

    public Qwen25OmniRuntimeService(ManagedModelLibraryStore libraryStore)
    {
        _libraryStore = libraryStore;
    }

    public ImageAnalysisHeavyResourcePlan? CurrentPlan { get; private set; }

    public string RuntimeVersion { get; private set; } = string.Empty;

    public string DeviceMapJson { get; private set; } = string.Empty;

    public string RuntimeProfile { get; private set; } = string.Empty;

    public string AttentionImplementation { get; private set; } = string.Empty;

    public bool IsWorkerRunning
    {
        get
        {
            lock (_processSync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public bool IsReady => IsWorkerRunning
        && RuntimeProfile is "thinker" or "omni";

    public async Task<OmniWarmupResult> PrepareAsync(
        Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress,
        CancellationToken cancellationToken,
        bool reuseCurrentPlan = false)
    {
        await ComponentLicenseGate.EnsureAsync(ManagedModelCatalog.Qwen25OmniHeavyArtifactId, cancellationToken);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var card = ResolveReadyCard();
        progress?.Report(new ImageAnalysisLiteraryProgress(
            ManagedModelRoles.Core,
            "planning",
            "Measuring RAM, VRAM and commit before the Heavy warmup."));
        var plan = reuseCurrentPlan && CurrentPlan is not null
            ? CurrentPlan
            : await _resourcePlanning.MeasureAndPlanAsync(cancellationToken).ConfigureAwait(false);
        CurrentPlan = plan;
        log(DescribePlan(plan));
        if (!plan.HasEnoughGpuMemory)
        {
            // Temporary user-approved experiment (2026-09-04): measure actual fit
            // instead of rejecting below 14 GiB. The worker's CUDA-only validation stays enabled.
            // throw new InvalidOperationException(
            //     "Heavy requires at least 14 GiB of calculated free GPU memory for Qwen2.5-Omni-3B. " +
            //     "Close another GPU-heavy program and restart Heavy. CPU offload is disabled for this model.");
            log("Heavy test override: 14 GiB precheck bypassed; measured budgets and CUDA-only validation remain active.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWorkerStarted();
            var probe = await SendLockedAsync(
                new { command = "probe", modelDirectory = card.InstallDirectory },
                streamProgress: null,
                cancellationToken).ConfigureAwait(false);
            EnsureProbeCompatible(probe);
            RuntimeVersion = $"python={probe.PythonVersion}; torch={probe.TorchVersion}; transformers={probe.TransformersVersion}; accelerate={probe.AccelerateVersion}; qwen-omni-utils={probe.QwenOmniUtilsVersion}; flash-attn={probe.FlashAttentionVersion}; torch-flash-sdpa={probe.TorchFlashSdpaEnabled}";
            progress?.Report(new ImageAnalysisLiteraryProgress(
                ManagedModelRoles.Core,
                "warming",
                "Loading the Qwen2.5-Omni-3B text Thinker entirely on the GPU."));
            var beforeSample = await _resourcePlanning
                .CaptureCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            var before = RuntimeResourceDiagnostics.DescribeSystemMemory("before_omni_warmup");
            log(before);
            var response = await SendLockedAsync(
                new
                {
                    command = "warmup",
                    modelDirectory = card.InstallDirectory,
                    cpuBudgetBytes = plan.CpuBudgetBytes,
                    gpuBudgetBytes = plan.GpuBudgetBytes
                },
                streamProgress: null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            UpdateRuntimeState(response);
            log($"Omni Thinker warmup completed: loadMilliseconds={response.LoadMilliseconds}; profile={RuntimeProfile}; attention={AttentionImplementation}; deviceMap={DeviceMapJson}.");
            log(RuntimeResourceDiagnostics.DescribeSystemMemory("after_omni_warmup"));
            var afterSample = await _resourcePlanning
                .CaptureCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            _postWarmupSample = afterSample;
            var peak = SafePeakWorkingSet();
            return new OmniWarmupResult(
                response.AlreadyLoaded,
                response.LoadMilliseconds,
                plan,
                RuntimeVersion,
                DeviceMapJson,
                peak,
                beforeSample.AvailableRamBytes,
                afterSample.AvailableRamBytes,
                beforeSample.CommitAvailableBytes,
                afterSample.CommitAvailableBytes,
                beforeSample.AvailableVramBytes,
                afterSample.AvailableVramBytes);
        }
        catch
        {
            StopWorker();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(
        CancellationToken cancellationToken)
    {
        var plan = CurrentPlan
            ?? throw new InvalidOperationException("The Heavy placement plan is unavailable.");
        var baseline = _postWarmupSample
            ?? throw new InvalidOperationException("The Heavy post-warmup resource baseline is unavailable.");
        var current = await _resourcePlanning.CaptureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return _resourcePlanning.EvaluatePostWarmupPressure(plan, baseline, current);
    }

    public async Task<OmniTextGenerationResult> GenerateAsync(
        string command,
        string imagePath,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation,
        IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken cancellationToken,
        Action<string>? responseReceived = null,
        Action<string>? diagnosticReceived = null)
    {
        if (command is not ("analyze" or "compose" or "revise"))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureWorkerRunning();
            DescribeRequestMemory(command + "_before", diagnosticReceived);
            var response = await SendLockedAsync(
                new
                {
                    command,
                    imagePath,
                    messages = conversation.Select(message => new
                    {
                        role = message.Role,
                        content = message.Content,
                        includesImage = message.IncludesImage
                    }).ToList()
                },
                streamProgress,
                cancellationToken,
                diagnosticReceived).ConfigureAwait(false);
            responseReceived?.Invoke(response.RawProtocol);
            DescribeRequestMemory(command + "_after", diagnosticReceived);
            EnsureSuccess(response);
            UpdateRuntimeState(response);
            if (!string.Equals(response.FinishReason, "eos", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Omni did not finish the hidden response with EOS.");
            }
            return new OmniTextGenerationResult(
                response.Content,
                response.ElapsedMilliseconds,
                response.InputTokens,
                response.GeneratedTokens,
                response.MaxContextTokens,
                response.FinishReason,
                response.PreprocessingMilliseconds,
                response.GenerationMilliseconds,
                response.TimeToFirstTokenMilliseconds,
                response.DecodeTokensPerSecond,
                response.ProfileSwitchMilliseconds,
                response.RuntimeProfile,
                response.AttentionImplementation,
                string.Join(",", response.EosTokenIds),
                response.LastTokenId,
                response.RawProtocol);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            StopWorker();
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OmniSpeechGenerationResult> SpeakAsync(
        string text,
        string speaker,
        int volume,
        int ratePercent,
        IProgress<OmniSpeechProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var audioPath = CreateAudioPath();
        try
        {
            EnsureWorkerRunning();
            progress?.Report(new OmniSpeechProgress("synthesizing"));
            var response = await SendLockedAsync(
                new
                {
                    command = "speak",
                    text,
                    speaker,
                    outputPath = audioPath,
                    volume = Math.Clamp(volume, 0, 100) / 100d,
                    speed = Math.Clamp(ratePercent, 70, 160) / 100d
                },
                streamProgress: null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            UpdateRuntimeState(response);
            if (!File.Exists(response.AudioPath))
            {
                throw new InvalidDataException("Omni Talker reported success without an audio file.");
            }
            progress?.Report(new OmniSpeechProgress("ready"));
            return new OmniSpeechGenerationResult(
                true,
                response.AudioPath,
                response.GenerationMilliseconds,
                response.TimeToFirstAudioMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(audioPath);
            return new OmniSpeechGenerationResult(false, string.Empty, 0, 0, ex.Message);
        }
        catch (OperationCanceledException)
        {
            StopWorker();
            TryDelete(audioPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_processSync)
        {
            process = _process;
            _process = null;
        }
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new
                {
                    id = Interlocked.Increment(ref _nextRequestId),
                    payload = new { command = "shutdown" }
                }, JsonOptions));
                process.StandardInput.Flush();
                if (!process.WaitForExit(4_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4_000);
                }
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
        process.Dispose();
        _errorDrainTask = null;
        CurrentPlan = null;
        _postWarmupSample = null;
        RuntimeVersion = string.Empty;
        DeviceMapJson = string.Empty;
        RuntimeProfile = string.Empty;
        AttentionImplementation = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        _gate.Dispose();
    }

    private async Task<OmniWorkerResponse> SendLockedAsync(
        object payload,
        IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken cancellationToken,
        Action<string>? diagnosticReceived = null)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("The isolated Qwen2.5-Omni worker is not running.");
        }
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var request = JsonSerializer.Serialize(new { id = requestId, payload }, JsonOptions);
        await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException(
                    "The isolated Qwen2.5-Omni worker returned no response. " + GetStandardErrorTail());
            }
            var response = JsonSerializer.Deserialize<OmniWorkerResponse>(line, JsonOptions)
                ?? throw new InvalidDataException("The isolated Qwen2.5-Omni worker returned invalid JSON.");
            response.RawProtocol = line;
            if (response.Id != requestId)
            {
                var workerError = string.IsNullOrWhiteSpace(response.Error)
                    ? string.Empty
                    : $" Worker error: {response.Error}";
                throw new InvalidDataException(
                    $"The isolated Qwen2.5-Omni worker returned response id {response.Id} " +
                    $"while id {requestId} was expected.{workerError}");
            }
            if (string.Equals(response.Event, "diagnostic", StringComparison.Ordinal))
            {
                diagnosticReceived?.Invoke($"Omni diagnostic: requestId={requestId}; {response.Diagnostics}");
                continue;
            }
            if (string.Equals(response.Event, "stream", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(response.Text))
                {
                    streamProgress?.Report(new ModelStreamChunk(response.Text));
                }
                continue;
            }
            return response;
        }
    }

    private void EnsureWorkerStarted()
    {
        if (_process is { HasExited: false })
        {
            return;
        }
        StopWorker();
        var pythonPath = FindUpward("Runtime", "Python", "qwen3-omni", ".venv", "Scripts", "python.exe");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "qwen25_omni_worker.py");
        if (!File.Exists(scriptPath))
        {
            scriptPath = FindUpward("Исходники", "AIHub", "Tools", "qwen25_omni_worker.py") ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(pythonPath) || string.IsNullOrWhiteSpace(scriptPath))
        {
            throw new FileNotFoundException(
                "The isolated Heavy Python runtime was not found in Runtime\\Python\\qwen3-omni\\.venv.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = ProtocolEncoding,
            StandardOutputEncoding = ProtocolEncoding,
            StandardErrorEncoding = ProtocolEncoding
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["HF_DATASETS_OFFLINE"] = "1";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        var process = OwnedProcessRegistry.Shared.Start(startInfo, "Qwen25OmniRuntimeService")
            ?? throw new InvalidOperationException("The isolated Qwen2.5-Omni worker could not be started.");
        lock (_processSync)
        {
            _process = process;
        }
        _errorDrainTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_standardErrorTail)
                    {
                        _standardErrorTail.Enqueue(line);
                        while (_standardErrorTail.Count > 40)
                        {
                            _standardErrorTail.Dequeue();
                        }
                    }
                }
            }
            catch
            {
            }
        });
    }

    private ManagedModelArtifactCard ResolveReadyCard()
    {
        var card = _libraryStore.Load(ManagedModelCatalog.Qwen25OmniHeavyArtifactId)
            ?? throw new InvalidOperationException("Qwen2.5-Omni is not registered in the LOPATA model library.");
        if (card.Status != ManagedModelStatuses.Installed)
        {
            throw new InvalidOperationException("Qwen2.5-Omni is not fully downloaded and verified.");
        }
        var root = Path.GetFullPath(card.InstallDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (card.Files.Where(file => file.IsRequired).Any(file =>
        {
            var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
            var info = new FileInfo(path);
            return !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !info.Exists
                || info.Length != file.SizeBytes;
        }))
        {
            throw new InvalidDataException("A verified Qwen2.5-Omni file is missing or has changed.");
        }
        return card;
    }

    private static void EnsureProbeCompatible(OmniWorkerResponse response)
    {
        EnsureSuccess(response);
        if (!response.ModelPresent)
        {
            throw new InvalidDataException("The Heavy worker cannot see the verified model files.");
        }
        if (!response.CudaAvailable)
        {
            throw new InvalidOperationException("The Heavy runtime requires a CUDA-capable GPU, but CUDA is unavailable.");
        }
        if (response.AccelerateVersion == "missing" || response.QwenOmniUtilsVersion == "missing")
        {
            throw new InvalidOperationException("The isolated Heavy runtime is missing Accelerate or qwen-omni-utils.");
        }
        if (!TryParseVersion(response.TransformersVersion, out var transformers)
            || transformers < new Version(5, 2, 0))
        {
            throw new InvalidOperationException("The isolated Heavy runtime requires Transformers 5.2.0 or newer.");
        }
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Split(['+', '-', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return Version.TryParse(normalized, out version!);
    }

    private static void EnsureSuccess(OmniWorkerResponse response)
    {
        if (response.Success)
        {
            return;
        }
        var message = string.IsNullOrWhiteSpace(response.Error)
            ? "The isolated Qwen2.5-Omni worker reported an unknown error."
            : response.Error;
        if (string.Equals(response.ErrorCode, "context_exhausted", StringComparison.Ordinal))
        {
            throw new ImageAnalysisContextExhaustedException(message);
        }
        throw new InvalidOperationException(message);
    }

    private void UpdateRuntimeState(OmniWorkerResponse response)
    {
        if (response.DeviceMap.ValueKind != JsonValueKind.Undefined)
        {
            DeviceMapJson = response.DeviceMap.GetRawText();
        }
        if (!string.IsNullOrWhiteSpace(response.RuntimeProfile))
        {
            RuntimeProfile = response.RuntimeProfile;
        }
        if (!string.IsNullOrWhiteSpace(response.AttentionImplementation))
        {
            AttentionImplementation = response.AttentionImplementation;
        }
    }

    private void EnsureWorkerRunning()
    {
        if (_process is not { HasExited: false })
        {
            throw new InvalidOperationException("Qwen2.5-Omni is not warmed up for the current Heavy session.");
        }
    }

    private void StopWorker()
    {
        Process? process;
        lock (_processSync)
        {
            process = _process;
            _process = null;
        }
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(4_000);
            }
        }
        catch
        {
        }
        process.Dispose();
    }

    private long SafePeakWorkingSet()
    {
        try
        {
            return _process is { HasExited: false } process
                ? RuntimeResourceDiagnostics.Capture(process).PeakWorkingSetBytes
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private string GetStandardErrorTail()
    {
        lock (_standardErrorTail)
        {
            return string.Join(" | ", _standardErrorTail);
        }
    }

    private static string DescribePlan(ImageAnalysisHeavyResourcePlan plan) =>
        $"Heavy placement plan: strategy={plan.Strategy}; samples={plan.Samples.Count}; " +
        $"availableRamBytes={plan.AvailableRamBytes}; availableVramBytes={plan.AvailableVramBytes}; " +
        $"commitAvailableBytes={plan.CommitAvailableBytes}; cpuBudgetBytes={plan.CpuBudgetBytes}; " +
        $"gpuBudgetBytes={plan.GpuBudgetBytes}; windowsReserveBytes={plan.WindowsReserveBytes}; " +
        $"gpuReserveBytes={plan.GpuReserveBytes}; gpuSufficient={plan.HasEnoughGpuMemory}.";

    private static string CreateAudioPath()
    {
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "Runtime", "ImageAnalysis", "OmniSpeech");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"omni-{Guid.NewGuid():N}.wav");
    }

    private static string? FindUpward(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static void TryDelete(string path)
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
        }
    }

    private void DescribeRequestMemory(string phase, Action<string>? log)
    {
        if (log is null || _process is null) return;
        try { log(RuntimeResourceDiagnostics.DescribeSnapshot("omni", _process, phase)); }
        catch (Exception ex) { log($"Omni memory sample unavailable: {ex.Message}"); }
    }

    private sealed class OmniWorkerResponse
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string RawProtocol { get; set; } = string.Empty;
        public string Diagnostics { get; set; } = string.Empty;
        public int Id { get; set; }
        public bool Success { get; set; }
        public string Event { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string FinishReason { get; set; } = string.Empty;
        public int[] EosTokenIds { get; set; } = [];
        public int? LastTokenId { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public int InputTokens { get; set; }
        public int GeneratedTokens { get; set; }
        public int MaxContextTokens { get; set; }
        public string PythonVersion { get; set; } = string.Empty;
        public string TorchVersion { get; set; } = string.Empty;
        public string TransformersVersion { get; set; } = string.Empty;
        public string AccelerateVersion { get; set; } = string.Empty;
        public string QwenOmniUtilsVersion { get; set; } = string.Empty;
        public string FlashAttentionVersion { get; set; } = string.Empty;
        public bool TorchFlashSdpaEnabled { get; set; }
        public bool ModelPresent { get; set; }
        public bool CudaAvailable { get; set; }
        public bool AlreadyLoaded { get; set; }
        public long LoadMilliseconds { get; set; }
        public JsonElement DeviceMap { get; set; }
        public string AudioPath { get; set; } = string.Empty;
        public long GenerationMilliseconds { get; set; }
        public long TimeToFirstAudioMilliseconds { get; set; }
        public long PreprocessingMilliseconds { get; set; }
        public long TimeToFirstTokenMilliseconds { get; set; }
        public double DecodeTokensPerSecond { get; set; }
        public long ProfileSwitchMilliseconds { get; set; }
        public string RuntimeProfile { get; set; } = string.Empty;
        public string AttentionImplementation { get; set; } = string.Empty;
    }
}

public sealed record OmniWarmupResult(
    bool AlreadyLoaded,
    long LoadMilliseconds,
    ImageAnalysisHeavyResourcePlan Plan,
    string RuntimeVersion,
    string DeviceMapJson,
    long PeakWorkingSetBytes,
    long RamBeforeWarmupBytes,
    long RamAfterWarmupBytes,
    long CommitBeforeWarmupBytes,
    long CommitAfterWarmupBytes,
    long VramBeforeWarmupBytes,
    long VramAfterWarmupBytes);

public sealed record OmniTextGenerationResult(
    string Content,
    long ElapsedMilliseconds,
    int InputTokens,
    int GeneratedTokens,
    int MaxContextTokens,
    string FinishReason,
    long PreprocessingMilliseconds,
    long GenerationMilliseconds,
    long TimeToFirstTokenMilliseconds,
    double DecodeTokensPerSecond,
    long ProfileSwitchMilliseconds,
    string RuntimeProfile,
    string AttentionImplementation,
    string EosTokenIds = "",
    int? LastTokenId = null,
    string RawProtocol = "");

public sealed class ImageAnalysisContextExhaustedException(string message, bool outputTruncated = false) : Exception(message)
{
    public bool OutputTruncated { get; } = outputTruncated;
}
