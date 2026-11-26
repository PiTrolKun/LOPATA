using System.ComponentModel;
using System.Diagnostics;
using Lopata.Inspector;

namespace Lopata.PcInspector;

internal sealed class InspectorObserver(InspectorStore store, Func<string, string, bool> show)
{
    private readonly Dictionary<(string Operation, int Attempt, Guid AttemptId), ObservedAttempt> _attempts = [];
    private readonly Dictionary<(int Pid, DateTimeOffset Start), (InspectorEvent Event, DateTimeOffset Due)> _releasing = [];
    private readonly Dictionary<string, DateTimeOffset> _diagnosedFailures = [];
    private TelemetryFrame? _frame;
    private DateTimeOffset _nextSample;
    private DateTimeOffset _nextRotation;

    internal async Task RunAsync(DateTimeOffset startedUtc, CancellationToken token)
    {
        using var telemetry = new WindowsInspectorTelemetry();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(165));
        do
        {
            if (store.StopRequested(startedUtc) || !store.ReadState().Enabled) return;
            var now = DateTimeOffset.UtcNow;
            try
            {
                if (_frame is null || now >= _nextSample)
                {
                    _frame = telemetry.Read(now);
                    SampleAttempts(_frame);
                    _nextSample = now + (_attempts.Values.Any(attempt => attempt.Active) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(12));
                }
                foreach (var (path, item) in store.PendingEvents())
                {
                    var age = now - item.TimestampUtc;
                    var live = age >= TimeSpan.FromSeconds(-20) && age <= TimeSpan.FromSeconds(6);
                    if (item.Kind == InspectorEventKind.Preflight && live &&
                        (_frame is null || now - _frame.Sample.TimestampUtc > TimeSpan.FromSeconds(2)))
                    {
                        _frame = telemetry.Read(now);
                        _nextSample = now + TimeSpan.FromSeconds(1);
                    }
                    var details = item.Kind == InspectorEventKind.Preflight && live && _frame is { } current
                        ? MeasurePreflight(item, current) : item;
                    store.Record(details);
                    var preflight = Apply(details, now, live);
                    store.CompleteEvent(path, details, preflight);
                }
                CheckReleased(now);
                if (now >= _nextRotation)
                {
                    store.Rotate(now);
                    foreach (var expired in _diagnosedFailures.Where(row => row.Value < now - InspectorStore.NoticeAge).ToArray())
                        _diagnosedFailures.Remove(expired.Key);
                    _nextRotation = now + TimeSpan.FromMinutes(3);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Recover on the next pulse if Windows holds a local file during update or scanning.
            }
        } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
    }

    private InspectorNotice? Apply(InspectorEvent item, DateTimeOffset now, bool live)
    {
        var key = (item.OperationId, item.Attempt, item.AttemptId);
        switch (item.Kind)
        {
            case InspectorEventKind.Preflight:
                if (live && _frame is { } fresh)
                {
                    _nextSample = now;
                    return CheckPreflight(item, fresh);
                }
                return null;
            case InspectorEventKind.Queued:
            case InspectorEventKind.Preparing:
            case InspectorEventKind.AttemptStarted:
            case InspectorEventKind.ProcessStarted:
            case InspectorEventKind.Progress:
            case InspectorEventKind.Saved:
                Track(item);
                if (live && item.Outcome == InspectorOutcome.CpuRetry)
                    Notice(item, "cpu_retry", canRate: false);
                if (item.Kind == InspectorEventKind.Saved && item.Outcome == InspectorOutcome.SaveFailure)
                {
                    _diagnosedFailures[item.OperationId] = now;
                    if (live) OutcomeNotice(item);
                    store.FinishNotices(item.OperationId, item.Attempt, item.Outcome, null, item.AttemptId);
                }
                break;
            case InspectorEventKind.AttemptFinished:
                InspectorExperience? experience = null;
                if (_attempts.Remove(key, out var completed))
                {
                    completed.Update(item);
                    experience = completed.Complete(item);
                    if (experience is not null) store.RecordExperience(experience);
                }
                if (live) OutcomeNotice(item);
                if (InspectorDiagnostics.IsFailure(item.Outcome))
                    _diagnosedFailures[item.OperationId] = now;
                store.FinishNotices(item.OperationId, item.Attempt, item.Outcome, experience, item.AttemptId);
                break;
            case InspectorEventKind.OperationFinished:
                foreach (var entry in _attempts.Keys.Where(entry => entry.Operation == item.OperationId).ToArray())
                    _attempts.Remove(entry);
                if (!_diagnosedFailures.Remove(item.OperationId) && live) OutcomeNotice(item);
                store.FinishNotices(item.OperationId, null, item.Outcome, null);
                break;
            case InspectorEventKind.ReleaseRequested:
                if (live && item.ProcessId > 0 && item.ProcessStartedUtc is { } started)
                    _releasing[(item.ProcessId, started)] = (item, now + TimeSpan.FromSeconds(10));
                break;
            case InspectorEventKind.ProcessExited:
                if (item.ProcessId > 0 && item.ProcessStartedUtc is { } ended)
                    _releasing.Remove((item.ProcessId, ended));
                if (live) OutcomeNotice(item);
                break;
            case InspectorEventKind.ReportFinished:
                if (live)
                    Notice(item, item.Outcome == InspectorOutcome.Success ? "report_ready" : "report_core_unavailable", canRate: false);
                break;
        }
        return null;
    }

    private void Track(InspectorEvent item)
    {
        var key = (item.OperationId, item.Attempt, item.AttemptId);
        if (!_attempts.TryGetValue(key, out var tracked))
        {
            tracked = new ObservedAttempt(item);
            _attempts.Add(key, tracked);
            if (tracked.Active) _nextSample = item.TimestampUtc;
        }
        else tracked.Update(item);
        if (item.Kind is InspectorEventKind.AttemptStarted or InspectorEventKind.ProcessStarted)
            _nextSample = item.TimestampUtc;
    }

    private InspectorNotice? CheckPreflight(InspectorEvent item, TelemetryFrame frame)
    {
        if (item.ExpectedRamBytes is > 0 && frame.Sample.AvailableRamBytes is { } available &&
            available < item.ExpectedRamBytes)
            return Notice(item, "ram_pressure", delegateToApp: item.UiErrorShown);

        if (item.PreflightStatus != InspectorPreflightStatus.Insufficient) return null;
        var competitor = item.Adapter is { } adapter ? frame.CompetingApplication(adapter, WorkerPids()) : null;
        return Notice(item, "gpu_preflight", competitor, delegateToApp: item.UiErrorShown);
    }

    private InspectorEvent MeasurePreflight(InspectorEvent item, TelemetryFrame frame)
    {
        if (item.Device is "cpu" or "remote") return item;
        if (item.PreflightStatus != InspectorPreflightStatus.Unknown)
        {
            if (item.Adapter is not null || !SameProcess(item.ProcessId, item.ProcessStartedUtc)) return item;
            var owned = frame.Consumers.Where(consumer => consumer.Pid == item.ProcessId && consumer.DedicatedBytes > 0)
                .Select(consumer => consumer.Adapter).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return owned is [var exact] ? item with { Adapter = exact } : item;
        }
        var resident = item.Warm == true && SameProcess(item.ProcessId, item.ProcessStartedUtc)
            ? frame.ResolveWorker(item.ProcessId) : null;
        var adapter = resident?.Adapter ?? item.Adapter;
        var requested = frame.Sample.Gpus.FirstOrDefault(gpu => gpu.Adapter == adapter);
        if (requested is null || requested.CapacityBytes is not > 0 || requested.DedicatedBytes is not >= 0) return item;
        var expected = EstimatePeak(item.ExpectedVideoBytes, ObservedAttempt.FindContext(item, requested), frame.Sample.TimestampUtc);
        if (expected is null || item.Warm == true && resident?.DedicatedBytes is not >= 0) return item;
        if (resident is { DedicatedBytes: { } bytes }) expected = Math.Max(0, expected.Value - bytes);
        var free = Math.Max(0, requested.CapacityBytes.Value - requested.DedicatedBytes.Value);
        return item with { Adapter = requested.Adapter, AvailableVideoBytes = free, ExpectedVideoBytes = expected,
            PreflightStatus = InspectorDiagnostics.CheckVideoMemory(requested.Adapter, expected, free) };
    }

    internal static bool WaitingForVideoMemory(InspectorGpuSample item, long? expected) =>
        expected is > 0 && item.CapacityBytes is > 0 && item.DedicatedBytes is { } dedicated &&
        Math.Max(0, item.CapacityBytes.Value - dedicated) < expected;

    private long? EstimatePeak(long? configured, string? context, DateTimeOffset now)
    {
        var measured = TypicalPeak(context, now);
        return measured is { } peak ? Math.Max(configured ?? 0, peak) : configured;
    }

    private long? TypicalPeak(string? context, DateTimeOffset now)
    {
        if (context is null) return null;
        var peaks = CompatibleSuccesses(context, now)
            .Where(item => item.PeakDedicatedBytes is > 0)
            .Select(item => item.PeakDedicatedBytes!.Value).Order().ToArray();
        if (peaks.Length < 4 || peaks[^1] > 2d * peaks[0]) return null;
        return peaks[peaks.Length / 2];
    }

    private double? TypicalDuration(ObservedAttempt attempt, DateTimeOffset now)
    {
        if (attempt.TypicalSeconds is { } cached && now - attempt.ReadTypicalAt < TimeSpan.FromMinutes(5)) return cached;
        if (attempt.Context is not { } context || now - attempt.ReadTypicalAt < TimeSpan.FromMinutes(5)) return null;
        attempt.ReadTypicalAt = now;
        var times = CompatibleSuccesses(context, now).Select(item => item.DurationSeconds).Order().ToArray();
        if (times.Length < 4 || times[^1] > Math.Max(5, 2 * times[0])) return null;
        attempt.TypicalSeconds = times[times.Length / 2];
        return attempt.TypicalSeconds;
    }

    private IEnumerable<InspectorExperience> CompatibleSuccesses(string context, DateTimeOffset now) =>
        store.ReadExperience(context, now)
            .Where(item => item.Outcome == InspectorOutcome.Success && item.Pressure == "normal" && item.DurationSeconds > 0);

    private bool FavorDirectAdvice(string? context, DateTimeOffset now)
    {
        if (context is null) return true;
        var votes = store.ReadExperience(context, now).Where(item => item.HelpfulCount + item.UnhelpfulCount > 0).ToArray();
        return votes.Length < 4 || votes.Sum(item => item.HelpfulCount) >= votes.Sum(item => item.UnhelpfulCount);
    }

    private HashSet<int> WorkerPids() => _attempts.Values.Select(value => value.Item.ProcessId)
        .Where(pid => pid > 0).Append(Environment.ProcessId).ToHashSet();

    private void SampleAttempts(TelemetryFrame frame)
    {
        var now = frame.Sample.TimestampUtc;
        var excluded = WorkerPids();
        var any = false;
        foreach (var (key, attempt) in _attempts.ToArray())
        {
            if (now - attempt.LastEventUtc > TimeSpan.FromHours(4))
            {
                _attempts.Remove(key);
                continue;
            }
            if (!attempt.Active) continue;
            var item = attempt.Item;
            var worker = SameProcess(item.ProcessId, item.ProcessStartedUtc) ? frame.ResolveWorker(item.ProcessId) : null;
            var adapter = worker is null ? frame.MatchAdapter(item.Adapter) : frame.MatchAdapter(worker.Adapter);
            var scarce = adapter is not null && WaitingForVideoMemory(adapter, 768L * 1024 * 1024);
            var competitor = worker is not null && (scarce || worker.SharedBytes is >= 1024L * 1024 * 1024)
                ? frame.CompetingApplication(worker.Adapter, excluded, scarce ? 768L * 1024 * 1024 : 1536L * 1024 * 1024)
                : null;
            var lowVideoMemory = scarce || competitor is not null;
            attempt.Observe(worker, adapter, lowVideoMemory);
            store.Record(frame.Sample with
            {
                OperationId = item.OperationId,
                Attempt = item.Attempt,
                AttemptId = item.AttemptId,
                WorkerProcessId = worker?.Pid ?? (item.ProcessId > 0 ? item.ProcessId : null),
                WorkerAdapter = adapter?.Adapter,
                WorkerDedicatedBytes = worker?.DedicatedBytes,
                WorkerSharedBytes = worker?.SharedBytes,
                WorkerUtilizationPercent = worker?.UtilizationPercent
            });
            any = true;
            if (competitor is not null && attempt.SustainedVideoPressure && attempt.ProgressObserved)
            {
                Notice(item, "gpu_pressure", competitor, during: true);
            }
            if (attempt.ProgressObserved && attempt.KnownStage &&
                now - attempt.StageStartedUtc > TimeSpan.FromSeconds(Math.Max(75, (TypicalDuration(attempt, now) ?? 0) * 1.5)))
                Notice(item, "long_stage", canRate: false);
        }
        if (!any) store.Record(frame.Sample);
    }

    private void CheckReleased(DateTimeOffset now)
    {
        foreach (var (key, release) in _releasing.Where(row => row.Value.Due <= now).ToArray())
        {
            _releasing.Remove(key);
            if (SameProcess(key.Pid, key.Start)) Notice(release.Event, "not_released", canRate: false);
        }
    }

    private static bool SameProcess(int pid, DateTimeOffset? started)
    {
        if (pid <= 0 || started is null) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == started.Value.UtcDateTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private void OutcomeNotice(InspectorEvent item)
    {
        if (item.UiErrorShown) return;
        var code = item.Outcome switch
        {
            InspectorOutcome.VideoMemoryAdmissionDenied => "vram_admission_denied",
            InspectorOutcome.OutOfVideoMemory => "out_of_video_memory",
            InspectorOutcome.OutOfMemory => "out_of_memory",
            InspectorOutcome.DriverFailure => "driver_failed",
            InspectorOutcome.WorkerFailure => "worker_failed",
            InspectorOutcome.SaveFailure => "save_failed",
            InspectorOutcome.Disconnected => "connection_failed",
            InspectorOutcome.Failure => "task_failed",
            _ => null
        };
        if (code is not null) Notice(item, code, canRate: false);
    }

    private InspectorNotice? Notice(InspectorEvent item, string code, string? competitor = null,
        bool during = false, bool canRate = true, bool delegateToApp = false)
    {
        var id = InspectorStore.Hash(item.OperationId + "\n" + item.Attempt + "\n" + item.AttemptId + "\n" + code
            + (code == "gpu_preflight" ? "\n" + item.Stage : ""));
        if (store.HasNotice(id)) return null;
        var russian = store.ReadRussian();
        var title = russian ? "Фоновый ИИ-инспектор ПК" : "Background PC inspector";
        var text = code == "gpu_preflight" && item.ExpectedVideoBytes is { } needed && item.AvailableVideoBytes is { } free
            ? InspectorDiagnostics.VideoMemoryWarning(needed, free, russian)
                + (competitor is null ? "" : " " + InspectorMessages.Get("gpu_pressure", competitor, item.Stage, false, russian))
            : InspectorDiagnostics.FailureExplanation(item.Outcome, russian)
                ?? InspectorMessages.Get(code, competitor, item.Stage, during, russian);
        var notice = new InspectorNotice(id, DateTimeOffset.UtcNow, item.OperationId, text,
            item.Outcome == InspectorOutcome.Unknown ? null : item.Outcome.ToString())
        { Code = code, Attempt = item.Attempt, AttemptId = item.AttemptId, CanRate = canRate, Shown = false };
        if (!store.RecordNotice(notice)) return null;
        if (!delegateToApp && show(title, text)) _ = store.ConfirmNoticeDisplayed(id);
        return notice;
    }
}
