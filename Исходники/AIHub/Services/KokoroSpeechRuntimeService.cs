using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class KokoroSpeechRuntimeService : IDisposable
{
    private const int MaximumStandardErrorLines = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ManagedModelLibraryStore _store;
    private readonly ImageAnalysisSpeechMemoryPolicy _memoryPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _playbackSync = new();
    private readonly object _standardErrorSync = new();
    private readonly Queue<string> _standardErrorTail = new();
    private string _workerIdentity = string.Empty;
    private Process? _process;
    private Task? _errorDrainTask;
    private SoundPlayer? _activePlayer;
    private string _loadedLanguage = string.Empty;
    private int _nextRequestId;
    private bool _disposed;

    public KokoroSpeechRuntimeService(
        ManagedModelLibraryStore store,
        ImageAnalysisSpeechMemoryPolicy? memoryPolicy = null)
    {
        _store = store;
        _memoryPolicy = memoryPolicy ?? new ImageAnalysisSpeechMemoryPolicy();
    }

    public bool IsModelInstalled(string? languageCode)
    {
        var card = _store.Load(ManagedModelCatalog.ResolveKokoroArtifactId(languageCode));
        return card is not null && HasExactFiles(card);
    }

    public bool IsWarm(string? languageCode)
    {
        var process = _process;
        return process is { HasExited: false }
            && string.Equals(
                _loadedLanguage,
                NormalizeLanguage(languageCode),
                StringComparison.Ordinal);
    }

    public string DescribeCurrentRuntime(string? languageCode, string phase)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return $"Runtime resources: component=Kokoro-{NormalizeLanguage(languageCode)}; " +
                $"phase={phase}; state=not_running.";
        }

        return RuntimeResourceDiagnostics.DescribeSnapshot(
            $"Kokoro-{NormalizeLanguage(languageCode)}",
            process,
            phase);
    }

    public string DescribeCurrentLaunch(string? languageCode)
    {
        var normalizedLanguage = NormalizeLanguage(languageCode);
        var process = _process;
        if (process is null || process.HasExited)
        {
            return $"Runtime launch: component=Kokoro-{normalizedLanguage}; state=not_running.";
        }

        var card = _store.Load(ManagedModelCatalog.ResolveKokoroArtifactId(normalizedLanguage));
        var modelFile = card?.Files
            .FirstOrDefault(file => string.Equals(
                file.Purpose,
                "model_weights",
                StringComparison.Ordinal));
        var modelPath = card is null || modelFile is null
            ? null
            : Path.Combine(card.InstallDirectory, modelFile.RelativePath);
        return RuntimeResourceDiagnostics.DescribeLaunch(
                $"Kokoro-{normalizedLanguage}",
                process,
                "CPU/RAM only; CUDA is not requested by AI HUB",
                modelPath)
            + $" artifactStoredBytes={card?.StoredBytes ?? 0}.";
    }

    public async Task<KokoroWarmupResult> WarmAsync(
        string languageCode,
        bool forceMemoryAttempt,
        long pendingAllocationBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new KokoroWarmupResult(KokoroWarmupCodes.Cancelled);
        }
        try
        {
            return await EnsureWarmLockedAsync(
                NormalizeLanguage(languageCode),
                forceMemoryAttempt,
                pendingAllocationBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new KokoroWarmupResult(KokoroWarmupCodes.Cancelled);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KokoroSpeechResult> SpeakAsync(
        string languageCode,
        string text,
        int volumePercent,
        int ratePercent,
        IProgress<KokoroSpeechProgress>? progress,
        CancellationToken cancellationToken,
        bool generateOnly = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new KokoroSpeechResult(true, KokoroWarmupCodes.Ready);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? audioPath = null;
        bool retainAudio = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var normalizedLanguage = NormalizeLanguage(languageCode);
            if (!IsWarm(normalizedLanguage))
            {
                progress?.Report(new KokoroSpeechProgress(KokoroSpeechStages.Warming));
            }
            var warmup = await EnsureWarmLockedAsync(
                normalizedLanguage,
                forceMemoryAttempt: true,
                pendingAllocationBytes: 0,
                cancellationToken).ConfigureAwait(false);
            if (!warmup.IsReady)
            {
                return new KokoroSpeechResult(
                    false,
                    warmup.Code,
                    Error: warmup.Error,
                    ErrorStage: warmup.ErrorStage,
                    ErrorType: warmup.ErrorType,
                    StandardErrorTail: warmup.StandardErrorTail);
            }

            var process = _process!;
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.StartNew();
            var cpuSampler = new ProcessCpuSampler(process);
            audioPath = CreateAudioPath();
            progress?.Report(new KokoroSpeechProgress(KokoroSpeechStages.Synthesizing));
            KokoroWorkerResponse response;
            ProcessCpuSample cpuSample;
            try
            {
                response = await SendLockedAsync("synthesize", new
                {
                    command = "synthesize",
                    languageCode = normalizedLanguage,
                    text = text.Trim(),
                    outputPath = audioPath,
                    volume = Math.Clamp(volumePercent, 0, 100) / 100d,
                    speed = Math.Clamp(ratePercent, 70, 160) / 100d
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cpuSample = await cpuSampler.StopAsync().ConfigureAwait(false);
            }
            if (!response.Success || !File.Exists(audioPath))
            {
                var error = string.IsNullOrWhiteSpace(response.Error)
                    ? "The Kokoro worker did not create an audio file."
                    : response.Error;
                var failureResult = new KokoroSpeechResult(
                    false,
                    KokoroWarmupCodes.Failed,
                    PeakWorkingSetBytes: SafePeakWorkingSet(),
                    Error: error,
                    ErrorStage: response.ErrorStage,
                    ErrorType: response.ErrorType,
                    StandardErrorTail: CombineDiagnostics(
                        response.Diagnostics,
                        response.Traceback,
                        GetStandardErrorTail()));
                AppendMetric(new KokoroSpeechMetric(
                    DateTimeOffset.Now,
                    normalizedLanguage,
                    "speak",
                    false,
                    0,
                    response.GenerationMilliseconds,
                    0,
                    failureResult.PeakWorkingSetBytes,
                    0,
                    false,
                    response.ErrorCode,
                    ErrorStage: failureResult.ErrorStage,
                    ErrorType: failureResult.ErrorType,
                    Error: failureResult.Error,
                    Diagnostics: failureResult.StandardErrorTail));
                return failureResult;
            }

            process.Refresh();
            var cpuMilliseconds = Math.Max(
                0,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds);
            long firstAudioMilliseconds = started.ElapsedMilliseconds;
            if (!generateOnly)
            {
                using (var player = new SoundPlayer(audioPath))
                {
                    player.Load();
                    lock (_playbackSync)
                    {
                        _activePlayer = player;
                    }
                    firstAudioMilliseconds = started.ElapsedMilliseconds;
                    progress?.Report(new KokoroSpeechProgress(KokoroSpeechStages.Playing));
                    using var registration = cancellationToken.Register(StopPlayback);
                    await Task.Run(player.PlaySync, cancellationToken).ConfigureAwait(false);
                    lock (_playbackSync)
                    {
                        if (ReferenceEquals(_activePlayer, player))
                        {
                            _activePlayer = null;
                        }
                    }
                }

            }
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            var result = new KokoroSpeechResult(
                true,
                KokoroWarmupCodes.Ready,
                response.GenerationMilliseconds,
                firstAudioMilliseconds,
                SafePeakWorkingSet(),
                cpuMilliseconds,
                AverageCpuPercent: cpuSample.AveragePercent,
                PeakCpuPercent: cpuSample.PeakPercent,
                AudioPath: generateOnly ? audioPath : string.Empty);
            AppendMetric(new KokoroSpeechMetric(
                DateTimeOffset.Now,
                normalizedLanguage,
                "speak",
                false,
                0,
                result.GenerationMilliseconds,
                result.TimeToFirstAudioMilliseconds,
                result.PeakWorkingSetBytes,
                result.CpuMilliseconds,
                true,
                string.Empty,
                AverageCpuPercent: result.AverageCpuPercent,
                PeakCpuPercent: result.PeakCpuPercent));
            retainAudio = generateOnly;
            return result;
        }
        catch (OperationCanceledException)
        {
            StopPlayback();
            StopWorker();
            return new KokoroSpeechResult(false, KokoroWarmupCodes.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            AppendMetric(new KokoroSpeechMetric(
                DateTimeOffset.Now,
                NormalizeLanguage(languageCode),
                "speak",
                false,
                0,
                0,
                0,
                SafePeakWorkingSet(),
                0,
                false,
                "host_exception",
                ErrorStage: "host_speak",
                ErrorType: ex.GetType().Name,
                Error: ex.Message,
                Diagnostics: GetStandardErrorTail()));
            return new KokoroSpeechResult(
                false,
                KokoroWarmupCodes.Failed,
                PeakWorkingSetBytes: SafePeakWorkingSet(),
                Error: ex.Message,
                ErrorStage: "host_speak",
                ErrorType: ex.GetType().Name,
                StandardErrorTail: GetStandardErrorTail());
        }
        finally
        {
            if (!retainAudio && !string.IsNullOrWhiteSpace(audioPath))
            {
                TryDelete(audioPath);
            }
            _gate.Release();
        }
    }

    public void StopPlayback()
    {
        lock (_playbackSync)
        {
            try
            {
                _activePlayer?.Stop();
            }
            catch (InvalidOperationException)
            {
            }
            _activePlayer = null;
        }
    }

    public void Stop()
    {
        StopPlayback();
        StopWorker();
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

    private async Task<KokoroWarmupResult> EnsureWarmLockedAsync(
        string languageCode,
        bool forceMemoryAttempt,
        long pendingAllocationBytes,
        CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync(languageCode == "ru"
            ? ManagedModelCatalog.KokoroRussianArtifactId : ManagedModelCatalog.KokoroEnglishArtifactId, cancellationToken);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }
            && string.Equals(_loadedLanguage, languageCode, StringComparison.Ordinal))
        {
            return new KokoroWarmupResult(
                KokoroWarmupCodes.AlreadyReady,
                PeakWorkingSetBytes: SafePeakWorkingSet());
        }

        var card = _store.Load(ManagedModelCatalog.ResolveKokoroArtifactId(languageCode));
        if (card is null || !HasExactFiles(card))
        {
            return new KokoroWarmupResult(KokoroWarmupCodes.ModelMissing);
        }

        var memory = _memoryPolicy.EvaluateCurrent(pendingAllocationBytes);
        if (!memory.HasEnoughMemory && !forceMemoryAttempt)
        {
            return new KokoroWarmupResult(KokoroWarmupCodes.InsufficientMemory, memory);
        }

        var pythonPath = FindUpward("Runtime", "Python", "reranker", ".venv", "Scripts", "python.exe");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "kokoro_tts_worker.py");
        if (!File.Exists(scriptPath))
        {
            scriptPath = FindUpward("Исходники", "AIHub", "Tools", "kokoro_tts_worker.py") ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(pythonPath) || string.IsNullOrWhiteSpace(scriptPath))
        {
            return new KokoroWarmupResult(KokoroWarmupCodes.RuntimeMissing, memory);
        }

        var coldStart = _process is null || _process.HasExited;
        if (_process is { HasExited: false }
            && !string.Equals(_loadedLanguage, languageCode, StringComparison.Ordinal))
        {
            StopWorker();
            coldStart = true;
        }
        EnsureWorkerStarted(pythonPath, scriptPath);

        try
        {
            var process = _process!;
            var cpuSampler = new ProcessCpuSampler(process);
            KokoroWorkerResponse response;
            ProcessCpuSample cpuSample;
            try
            {
                response = await SendLockedAsync("load", new
                {
                    command = "load",
                    languageCode,
                    modelDirectory = card.InstallDirectory
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cpuSample = await cpuSampler.StopAsync().ConfigureAwait(false);
            }
            if (!response.Success)
            {
                var code = response.ErrorCode == "runtime_missing"
                    ? KokoroWarmupCodes.RuntimeMissing
                    : KokoroWarmupCodes.Failed;
                var diagnostics = CombineDiagnostics(
                    response.Diagnostics,
                    response.Traceback,
                    GetStandardErrorTail());
                StopWorker();
                AppendMetric(new KokoroSpeechMetric(
                    DateTimeOffset.Now,
                    languageCode,
                    "warmup",
                    coldStart,
                    response.LoadMilliseconds,
                    0,
                    0,
                    0,
                    0,
                    false,
                    response.ErrorCode,
                    ErrorStage: response.ErrorStage,
                    ErrorType: response.ErrorType,
                    Error: response.Error,
                    AverageCpuPercent: cpuSample.AveragePercent,
                    PeakCpuPercent: cpuSample.PeakPercent,
                    Diagnostics: diagnostics));
                return new KokoroWarmupResult(
                    code,
                    memory,
                    Error: response.Error,
                    ErrorStage: response.ErrorStage,
                    ErrorType: response.ErrorType,
                    StandardErrorTail: diagnostics,
                    AverageCpuPercent: cpuSample.AveragePercent,
                    PeakCpuPercent: cpuSample.PeakPercent);
            }

            _loadedLanguage = languageCode;
            var peak = SafePeakWorkingSet();
            AppendMetric(new KokoroSpeechMetric(
                DateTimeOffset.Now,
                languageCode,
                "warmup",
                coldStart,
                response.LoadMilliseconds,
                0,
                0,
                peak,
                0,
                true,
                string.Empty,
                AverageCpuPercent: cpuSample.AveragePercent,
                PeakCpuPercent: cpuSample.PeakPercent,
                Diagnostics: response.Diagnostics));
            return new KokoroWarmupResult(
                response.AlreadyLoaded ? KokoroWarmupCodes.AlreadyReady : KokoroWarmupCodes.Ready,
                memory,
                response.LoadMilliseconds,
                peak,
                AverageCpuPercent: cpuSample.AveragePercent,
                PeakCpuPercent: cpuSample.PeakPercent);
        }
        catch
        {
            StopWorker();
            throw;
        }
    }

    private void EnsureWorkerStarted(string pythonPath, string scriptPath)
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        StopWorker();
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("faulthandler");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["HF_HUB_OFFLINE"] = "1";
        startInfo.Environment["TRANSFORMERS_OFFLINE"] = "1";
        startInfo.Environment["HF_DATASETS_OFFLINE"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        var process = OwnedProcessRegistry.Shared.Start(startInfo, "KokoroSpeechRuntimeService")
            ?? throw new InvalidOperationException("The local Kokoro worker could not be started.");
        _process = process;
        _errorDrainTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    AddStandardErrorLine(process, line);
                }
            }
            catch
            {
            }
        });
    }

    private async Task<KokoroWorkerResponse> SendLockedAsync(
        string command,
        object payload,
        CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            throw new InvalidOperationException("The local Kokoro worker is not running.");
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        if (process.HasExited)
        {
            return await CaptureWorkerFailureAsync(process, requestId, command, cancellationToken).ConfigureAwait(false);
        }
        var request = JsonSerializer.Serialize(new
        {
            id = requestId,
            payload
        }, JsonOptions);
        string? line;
        try
        {
            await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await CaptureWorkerFailureAsync(process, requestId, command, cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(line))
        {
            return await CaptureWorkerFailureAsync(process, requestId, command, cancellationToken).ConfigureAwait(false);
        }

        var response = JsonSerializer.Deserialize<KokoroWorkerResponse>(line, JsonOptions)
            ?? throw new InvalidOperationException("The local Kokoro worker returned an invalid response.");
        if (response.Id != requestId)
        {
            throw new InvalidOperationException("The local Kokoro worker returned an out-of-order response.");
        }
        return response;
    }

    private async Task<KokoroWorkerResponse> CaptureWorkerFailureAsync(
        Process process, int requestId, string command, CancellationToken cancellationToken)
    {
        var failure = await KokoroWorkerFailureDiagnostics.CaptureAsync(
            process, _errorDrainTask, GetStandardErrorTail, cancellationToken).ConfigureAwait(false);
        var response = new KokoroWorkerResponse
        {
            Id = requestId,
            ErrorCode = "worker_no_response",
            ErrorStage = command,
            ErrorType = "WorkerProcessFailure",
            Error = failure.Error,
            Diagnostics = failure.StandardError
        };
        StopWorker();
        return response;
    }

    private void StopWorker()
    {
        var process = _process;
        _process = null;
        _loadedLanguage = string.Empty;
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2_000);
            }
        }
        catch
        {
        }
        process.Dispose();
        _errorDrainTask = null;
        ClearStandardErrorTail();
    }

    private static bool HasExactFiles(ManagedModelArtifactCard card) =>
        !string.IsNullOrWhiteSpace(card.InstallDirectory)
        && card.Files.Where(file => file.IsRequired).All(file =>
        {
            var path = Path.GetFullPath(Path.Combine(card.InstallDirectory, file.RelativePath));
            var root = Path.GetFullPath(card.InstallDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var info = new FileInfo(path);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && info.Exists
                && info.Length == file.SizeBytes;
        });

    private static string NormalizeLanguage(string? languageCode) =>
        languageCode?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "ru";

    private static string CreateAudioPath()
    {
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "Runtime", "ImageAnalysis", "Speech");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"kokoro-{Guid.NewGuid():N}.wav");
    }

    private long SafePeakWorkingSet()
    {
        try
        {
            var process = _process;
            return process is null || process.HasExited
                ? 0
                : RuntimeResourceDiagnostics.Capture(process).PeakWorkingSetBytes;
        }
        catch
        {
            return 0;
        }
    }

    private void AddStandardErrorLine(Process process, string line)
    {
        var normalized = line.Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        if (normalized.Length > 500)
        {
            normalized = normalized[..500];
        }

        lock (_standardErrorSync)
        {
            if (!ReferenceEquals(process, _process))
            {
                return;
            }
            if (normalized.StartsWith("kokoro_worker_started; pythonPid=", StringComparison.Ordinal))
            {
                _workerIdentity = normalized;
                return;
            }
            _standardErrorTail.Enqueue(normalized);
            while (_standardErrorTail.Count > MaximumStandardErrorLines)
            {
                _standardErrorTail.Dequeue();
            }
        }
    }

    private void ClearStandardErrorTail()
    {
        lock (_standardErrorSync)
        {
            _standardErrorTail.Clear();
            _workerIdentity = string.Empty;
        }
    }

    private string GetStandardErrorTail()
    {
        lock (_standardErrorSync)
        {
            return CombineDiagnostics(_workerIdentity, string.Join(" | ", _standardErrorTail));
        }
    }

    private static string CombineDiagnostics(params string?[] values) =>
        string.Join(
            " | ",
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));

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

    private static void AppendMetric(KokoroSpeechMetric metric)
    {
        try
        {
            var directory = Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "kokoro-metrics.jsonl"),
                JsonSerializer.Serialize(metric, JsonOptions) + Environment.NewLine);
        }
        catch
        {
            // Metrics are diagnostic and must never break speech or analysis.
        }
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

    private sealed class KokoroWorkerResponse
    {
        public int Id { get; set; }
        public bool Success { get; set; }
        public bool AlreadyLoaded { get; set; }
        public long LoadMilliseconds { get; set; }
        public long GenerationMilliseconds { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorStage { get; set; } = string.Empty;
        public string ErrorType { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string Diagnostics { get; set; } = string.Empty;
        public string Traceback { get; set; } = string.Empty;
    }

    private sealed record ProcessCpuSample(double AveragePercent, double PeakPercent);

    private sealed class ProcessCpuSampler
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Task _samplingTask;
        private TimeSpan _previousCpu;
        private long _previousMilliseconds;
        private double _sum;
        private double _peak;
        private int _count;

        public ProcessCpuSampler(Process process)
        {
            _process = process;
            _process.Refresh();
            _previousCpu = _process.TotalProcessorTime;
            _samplingTask = Task.Run(SampleLoopAsync);
        }

        public async Task<ProcessCpuSample> StopAsync()
        {
            _cancellation.Cancel();
            try
            {
                await _samplingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            Sample();
            _cancellation.Dispose();
            return new ProcessCpuSample(
                _count == 0 ? 0 : _sum / _count,
                _peak);
        }

        private async Task SampleLoopAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(100, _cancellation.Token).ConfigureAwait(false);
                Sample();
            }
        }

        private void Sample()
        {
            try
            {
                _process.Refresh();
                var elapsedMilliseconds = _clock.ElapsedMilliseconds;
                var elapsed = elapsedMilliseconds - _previousMilliseconds;
                if (elapsed <= 0)
                {
                    return;
                }
                var cpu = _process.TotalProcessorTime;
                var cpuMilliseconds = (cpu - _previousCpu).TotalMilliseconds;
                var percent = Math.Clamp(
                    cpuMilliseconds / elapsed / Environment.ProcessorCount * 100,
                    0,
                    100);
                _previousCpu = cpu;
                _previousMilliseconds = elapsedMilliseconds;
                _sum += percent;
                _peak = Math.Max(_peak, percent);
                _count++;
            }
            catch
            {
            }
        }
    }
}
