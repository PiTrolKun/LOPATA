using System.Text.Json;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private ILetItOutChatResident? _resident;
    private CancellationTokenSource? _warmCancel;
    private string? _warmOperationId;
    private bool _warmAttempted, _warmDeferred, _disposed;
    internal bool IsWarming => _warmCancel is not null;

    internal void ConfigureWarmup(ILetItOutChatResident resident)
    {
        _resident = resident;
        RequestWarmup();
    }

    private void WarmVisibilityChanged()
    {
        _warmAttempted = false; _warmDeferred = false;
        if (IsVisible){QueueMemoryYield();RequestWarmup();} else CancelWarmup();
        if (!IsVisible) { SaveConversationForExit(); EndMemoryRun(); }
        RequestMemoryClose();
        QueueContextRefresh(); ContextInfoChanged?.Invoke();
    }

    private void CancelWarmup() => _warmCancel?.Cancel();

    private void RequestWarmup()
    {
        if (_disposed || !IsLoaded || !IsVisible || _resident is null || _work is null
            || _requiresPreparation || IsWorking || IsWarming || MemoryIsBlocking || _warmAttempted) return;
        // A restored/private conversation can be revealed before its saved session id is applied.
        // Do not start a new load while any chat turn or warmup awaits an explicit resume.
        if (_background?.PendingOperations.Any(state => state.Kind is LetItOutChatWork.Kind or LetItOutChatWarmup.Kind
            ||state.Kind is LetItOutChatRuntime.MemoryWorkKind or LetItOutChatRuntime.TreeWorkKind&&!ParkedMemory(state)) == true) return;
        if (_background?.BlocksNewWork == true)
        { _warmDeferred = true; Availability(); return; }
        var path = _corePath();
        if (string.IsNullOrWhiteSpace(path)) return;
        _warmAttempted = true; _warmDeferred = false;
        _ = RunWarmupAsync(new(path, _language()), null, ApplicationBackgroundOperations.ExitToken);
    }

    public void ShowWarmupPending(BackgroundOperationState state)
    {
        if (IsWorking || IsWarming) return;
        AdoptConversation(state.Project ?? _sessionId); _sessionId = state.Project ?? _sessionId; _warmOperationId = state.Id;
        _warmAttempted = true; _warmDeferred = false; _statusKey = "LetItOut.WarmPaused";
        RenderHistory(); RefreshLibrary(); Availability();
    }

    public Task ResumeWarmupAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (IsWorking || IsWarming) return Task.CompletedTask;
        var request = state.Input.Deserialize<LetItOutWarmupRequest>()
            ?? throw new InvalidOperationException("LetItOut.InvalidCheckpoint");
        AdoptConversation(state.Project ?? _sessionId); _sessionId = state.Project ?? _sessionId; _warmAttempted = true; _warmDeferred = false;
        return RunWarmupAsync(request, state, token);
    }

    private async Task RunWarmupAsync(LetItOutWarmupRequest request, BackgroundOperationState? restored, CancellationToken token)
    {
        if (_resident is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _warmCancel = cancellation; _warmOperationId = restored?.Id ?? Guid.NewGuid().ToString("N");
        _statusKey = "LetItOut.Warming"; Availability();
        try
        {
            // Show the chat and its loading state before auditing files or loading weights.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            await LetItOutChatWarmup.RunAsync(_warmOperationId, _sessionId, request, _resident, cancellation.Token, restored);
            _background?.Acknowledge(_warmOperationId);
            _statusKey = "LetItOut.Ready";
        }
        catch (OperationCanceledException) { _statusKey = "LetItOut.WarmStopped"; ShowMode("Auto"); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _statusKey = IsMemoryStatus(error.Message) ? error.Message : "LetItOut.WarmFailed";
            ShowMode("Auto");
        }
        finally
        {
            _warmCancel = null;
            if (_memoryCloseRequested) EndMemoryRun();
            if (!_disposed) { Availability(); RequestWarmup(); RequestMemoryClose(); QueueContextRefresh(); }
        }
    }

    private string? WarmupStatus(BackgroundOperationState? state)
    {
        if (state?.Kind == LetItOutChatWarmup.Kind && state.Project == _sessionId)
        {
            if (state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Pausing) return "LetItOut.WarmPaused";
            if (state.Phase == BackgroundOperationPhase.Waiting)
                return IsMemoryStatus(state.Detail) ? state.Detail : "LetItOut.WarmFailed";
        }
        return IsWarming ? "LetItOut.Warming" : _warmDeferred ? "LetItOut.WarmDeferred" : null;
    }
}
