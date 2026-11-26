using System.Diagnostics;
using System.Runtime.CompilerServices;
using AIHub.Models;
using Lopata.Inspector;

namespace AIHub.Services;

internal static class ApplicationInspector
{
    internal const string Deferred = "Inspector.Preflight.Deferred";
    private sealed class Operation(BackgroundOperationState state)
    {
        internal string Id => state.Id;
        internal string Scenario => state.Kind;
        internal bool Private => state.Private;
        internal int Attempts = state.InspectorAttempts;
        internal InspectorEvent? LastIssue = state.LastInspectorFailure;
    }
    private static readonly AsyncLocal<Operation?> Active = new();
    private static readonly AsyncLocal<bool> Suppressed = new();
    private static readonly AsyncLocal<bool> Unattended = new();
    private static readonly ConditionalWeakTable<Process, GenerationInspectorAttempt> Workers = new();

    internal static InspectorStore? Store { get; private set; }
    internal static Func<CancellationToken, Task<bool>>? CanConfirmPreflight { get; set; }
    internal static Func<InspectorPreflightResult, CancellationToken, Task<bool>>? ConfirmPreflight { get; set; }

    internal static InspectorStore Initialize() => Store ??= new InspectorStore(AppDataPaths.BaseDirectory);
    internal static bool CheckEnabled
    {
        get
        {
            try { return Store?.ReadState().Enabled == true; }
            catch (Exception error) { LogUnavailable(error); return false; }
        }
    }

    internal static IDisposable Enter(BackgroundOperationState state)
    {
        var previous = Active.Value;
        Active.Value = new(state);
        return new ResetScope(() => Active.Value = previous);
    }

    internal static IDisposable? EnsureOperation(string scenario) => Active.Value is not null ? null :
        Enter(new BackgroundOperationState { Kind = scenario, Title = scenario, Private = Suppressed.Value });

    private sealed class ResetScope(Action reset) : IDisposable
    {
        public void Dispose() => reset();
    }

    internal static IDisposable PrivateScope()
    {
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        return new ResetScope(() => Suppressed.Value = previous);
    }

    internal static IDisposable UnattendedScope()
    {
        var previous = Unattended.Value;
        Unattended.Value = true;
        return new ResetScope(() => Unattended.Value = previous);
    }

    internal static int AttemptCount(string id) => Active.Value is { } operation && operation.Id == id
        ? Volatile.Read(ref operation.Attempts) : 0;

    internal static InspectorEvent? LastFailure(string id) => Active.Value is { } operation && operation.Id == id
        ? Volatile.Read(ref operation.LastIssue) : null;

    internal static InspectorEvent? NewAttempt(string scenario) => new()
    {
        OperationId = Active.Value?.Id ?? Guid.NewGuid().ToString("N"),
        Scenario = Suppressed.Value || Active.Value?.Private == true ? "private" : Active.Value?.Scenario ?? scenario,
        InMemoryOnly = Suppressed.Value || Active.Value?.Private == true,
        Attempt = Active.Value is { } operation ? Interlocked.Increment(ref operation.Attempts) : 1,
        AttemptId = Guid.NewGuid()
    };

    internal static async Task<GenerationInspectorAttempt> BeginAttemptAsync(string engine, string? backend,
        string? device, CancellationToken token, long? needed = null, long? free = null, string? model = null,
        string? stage = null, string? adapter = null)
    {
        var item = NewAttempt(engine);
        var attempt = new GenerationInspectorAttempt(item is null ? null : item with
        {
            Engine = engine, Backend = backend, Device = device, Model = model, Adapter = adapter,
            Stage = stage ?? "preparing"
        });
        await attempt.CheckAsync(stage ?? "preparing", needed, free, token);
        attempt.Emit(InspectorEventKind.AttemptStarted);
        return attempt;
    }

    internal static async Task<Process> StartWorkerAsync(ProcessStartInfo info, string component, CancellationToken token,
        string? backend = null, string? device = null, string? model = null, bool inspect = true,
        long? needed = null, long? free = null)
    {
        if (!inspect) return OwnedProcessRegistry.Shared.Start(info, component);
        var attempt = await BeginAttemptAsync(component, backend, device, token, needed, free, model, "model_loading");
        try
        {
            var process = attempt.Attach(OwnedProcessRegistry.Shared.Start(info, component));
            Workers.Add(process, attempt);
            return process;
        }
        catch (Exception error)
        {
            await attempt.FailAsync(error, token);
            throw;
        }
    }

    internal static void WorkerFinished(Process process)
    {
        if (Workers.TryGetValue(process, out var attempt) && Workers.Remove(process)) attempt.Succeed();
    }

    internal static async Task WorkerFailedAsync(Process process, Exception error, CancellationToken token,
        string? diagnostic = null, InspectorOutcome? confirmed = null)
    {
        if (!Workers.TryGetValue(process, out var attempt) || !Workers.Remove(process)) return;
        if (token.IsCancellationRequested || error is OperationCanceledException)
            confirmed = GenerationFailureClassifier.FromException(error, token);
        else if (confirmed is null && process.HasExited && process.ExitCode != 0)
            confirmed = GenerationFailureClassifier.FromWorker(process.ExitCode, diagnostic, attempt.Device);
        if (confirmed == InspectorOutcome.SaveFailure) attempt.Stage("output_write");
        await attempt.FailAsync(error, token, confirmed);
    }

    internal static InspectorEvent? DescribeImage(ImageGenerationRequest request, int attempt) =>
        Store is not null && NewAttempt("image.generate") is { } inspection
            ? inspection with
            {
                Model = request.ModelId,
                Width = request.Width,
                Height = request.Height
            }
            : null;

    internal static void StateChanged(BackgroundOperationState? previous, BackgroundOperationState next)
    {
        if (Store is null || next.Private || previous is { Id: var id, Phase: var phase }
            && id == next.Id && phase == next.Phase) return;
        var (kind, stage, outcome) = next.Phase switch
        {
            BackgroundOperationPhase.Running => (InspectorEventKind.Preparing, "preparing", InspectorOutcome.Unknown),
            BackgroundOperationPhase.Countdown => (InspectorEventKind.Queued, "queued", InspectorOutcome.Unknown),
            BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Paused =>
                (InspectorEventKind.Progress, "paused", InspectorOutcome.Unknown),
            BackgroundOperationPhase.Waiting => (InspectorEventKind.Progress, "waiting", InspectorOutcome.Unknown),
            BackgroundOperationPhase.Completed => (InspectorEventKind.OperationFinished, "completed", InspectorOutcome.Success),
            BackgroundOperationPhase.Canceled => (InspectorEventKind.OperationFinished, "cancelled", InspectorOutcome.Cancelled),
            BackgroundOperationPhase.Failed => (InspectorEventKind.OperationFinished, "failed", InspectorOutcome.Failure),
            _ => (InspectorEventKind.Progress, "unknown", InspectorOutcome.Unknown)
        };
        Publish(new()
        {
            OperationId = next.Id,
            Scenario = next.Kind,
            Kind = kind,
            Stage = stage,
            Outcome = outcome,
            TimestampUtc = next.UpdatedUtc
        });
    }

    internal static void Publish(InspectorEvent item)
    {
        if (item.InMemoryOnly || Store is not { } store) return;
        try { store.Publish(item); }
        catch (Exception error) { LogUnavailable(error); }
    }

    internal static async Task PreflightAsync(InspectorEvent item, CancellationToken token)
    {
        if (Store is not { } store) return;
        token.ThrowIfCancellationRequested();
        var status = InspectorDiagnostics.CheckVideoMemory(item.Device, item.ExpectedVideoBytes, item.AvailableVideoBytes);
        var mayWarn = status == InspectorPreflightStatus.Insufficient || status == InspectorPreflightStatus.Unknown
            && item.ExpectedVideoBytes is > 0 && (item.Warm == true && item.ProcessId > 0 || item.Adapter is not null);
        item = item with { Id = Guid.NewGuid(), Kind = InspectorEventKind.Preflight,
            PreflightStatus = status, TimestampUtc = DateTimeOffset.UtcNow };
        if (!mayWarn) { Publish(item); return; }
        InspectorPreflightResult result = new(false);
        var interactive = false;
        try
        {
            if (!store.ReadState().Enabled) return;
            interactive = !Unattended.Value && ConfirmPreflight is not null && CanConfirmPreflight is { } canConfirm && await canConfirm(token);
            if (!item.InMemoryOnly)
                result = await store.PublishPreflightAsync(item with { UiErrorShown = interactive }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { LogUnavailable(error); }
        if (status == InspectorPreflightStatus.Insufficient && result.Warning is null)
            result = result with { Status = status, Warning = InspectorDiagnostics.VideoMemoryWarning(
                item.ExpectedVideoBytes!.Value, item.AvailableVideoBytes!.Value, Russian()) };
        await ConfirmOrDeferAsync(result, interactive ? ConfirmPreflight : null, token);
    }

    internal static async Task ConfirmOrDeferAsync(InspectorPreflightResult result,
        Func<InspectorPreflightResult, CancellationToken, Task<bool>>? confirm, CancellationToken token)
    {
        if (result.Warning is { } warning && (confirm is null || !await confirm(result, token)))
            throw new InspectorDeferredException(warning, token);
    }

    internal static async Task<bool> IsForegroundAsync(CancellationToken token = default) => !Unattended.Value
        && CanConfirmPreflight is { } check && await check(token);

    internal static string? Explain(InspectorEvent? item) => item is null ? null :
        InspectorDiagnostics.FailureExplanation(item.Outcome, Russian());

    internal static async Task<InspectorEvent> ReportFailureAsync(Exception error, InspectorEvent item, CancellationToken token,
        InspectorOutcome? outcome = null)
    {
        outcome ??= GenerationFailureClassifier.FromException(error, token);
        var foreground = false;
        if (InspectorDiagnostics.IsFailure(outcome.Value))
        {
            try { foreground = await IsForegroundAsync(); }
            catch (Exception dispatchError) { LogUnavailable(dispatchError); }
        }
        var result = GenerationInspectorAttempt.RecordFailure(error, item, outcome.Value, foreground);
        if (InspectorDiagnostics.IsFailure(outcome.Value) && !item.InMemoryOnly
            && Active.Value is { } operation && operation.Id == item.OperationId)
            Volatile.Write(ref operation.LastIssue, result);
        return result;
    }

    private static bool Russian()
    {
        try { return Store?.ReadRussian() ?? true; }
        catch (Exception error) { LogUnavailable(error); return true; }
    }

    internal static void LogUnavailable(Exception error) =>
        OwnedProcessRegistry.Log("inspector_unavailable", "PCInspector", detail: error.GetType().Name);
}
