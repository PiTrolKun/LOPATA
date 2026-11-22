using AIHub.Models;
using Lopata.Inspector;

namespace AIHub.Services;

internal static class ApplicationInspector
{
    internal const string Deferred = "Inspector.Preflight.Deferred";
    private sealed record Operation(string Id, string Scenario, bool Private);
    private static readonly AsyncLocal<Operation?> Active = new();

    internal static InspectorStore? Store { get; private set; }
    internal static Func<CancellationToken, Task<bool>>? CanConfirmPreflight { get; set; }
    internal static Func<InspectorPreflightResult, CancellationToken, Task<bool>>? ConfirmPreflight { get; set; }

    internal static InspectorStore Initialize() => Store ??= new InspectorStore(AppDataPaths.BaseDirectory);

    internal static IDisposable Enter(BackgroundOperationState state)
    {
        var previous = Active.Value;
        Active.Value = new(state.Id, state.Kind, state.Private);
        return new ResetScope(previous);
    }

    private sealed class ResetScope(Operation? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }

    internal static InspectorEvent? DescribeImage(ImageGenerationRequest request, int attempt) =>
        Store is not null && Active.Value is { Private: false } operation
            ? new()
            {
                OperationId = operation.Id,
                Scenario = operation.Scenario,
                Model = request.ModelId,
                Width = request.Width,
                Height = request.Height,
                Attempt = attempt
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
        if (Store is not { } store) return;
        try { store.Publish(item); }
        catch (Exception error) { LogUnavailable(error); }
    }

    internal static async Task PreflightAsync(InspectorEvent item, CancellationToken token)
    {
        if (Store is not { } store) return;
        InspectorPreflightResult result;
        bool interactive;
        try
        {
            interactive = CanConfirmPreflight is { } canConfirm && await canConfirm(token);
            result = await store.PublishPreflightAsync(item with
            {
                Id = Guid.NewGuid(),
                Kind = InspectorEventKind.Preflight,
                UiErrorShown = interactive,
                TimestampUtc = DateTimeOffset.UtcNow
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { LogUnavailable(error); return; }
        if (interactive && result.Warning is not null && ConfirmPreflight is { } confirm
            && !await confirm(result, token))
            throw new OperationCanceledException(Deferred, token);
    }

    internal static void LogUnavailable(Exception error) =>
        OwnedProcessRegistry.Log("inspector_unavailable", "PCInspector", detail: error.GetType().Name);
}
