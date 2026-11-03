using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private string? _memoryRunId;
    private CancellationTokenSource? _memoryClosing;
    private bool _memoryCloseRequested;
    private Task? _memoryYield;
    private static bool ParkedMemory(BackgroundOperationState state)=>state.UserPaused&&!state.RequiresDecision
        &&state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting;
    private bool MemoryIsBlocking=>_memoryClosing is not null&&(_background is null||
        _background.PendingOperations.Any(state=>state.Kind==LetItOutChatRuntime.MemoryWorkKind&&!ParkedMemory(state)));

    private void QueueMemoryYield()
    {
        if(_disposed||!IsVisible||_background is null||_memoryYield is not null)return;
        _memoryYield=YieldMemoryAsync();
    }
    private async Task YieldMemoryAsync()
    {
        try
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var operations=_background?.PendingOperations.Where(s=>s.Kind==LetItOutChatRuntime.MemoryWorkKind&&!ParkedMemory(s)).ToArray()??[];
            if(operations.Length==0)return;
            foreach(var state in operations)
                await _background!.PauseAsync(state.Id);
            // A restored Waiting job may still require retirement confirmation. Preserve its checkpoint.
            if(_background is not null)await _background.RetryPausedRetirementAsync(ApplicationBackgroundOperations.RetireModelsAsync);
        }
        catch(Exception error)
        {OwnedProcessRegistry.Log("let_it_out_memory_yield_failed","LetItOut.Memory",detail:error.GetType().Name);}
        finally
        {
            _memoryYield=null;
            if(!_disposed){Availability();RequestWarmup();QueueContextRefresh();QueueTree();}
        }
    }

    private void EnsureMemoryRun()
    {
        if (_work?.HasMemory != true || _library is null || !_persisted || _memoryRunId is not null) return;
        _memoryRunId = _library.BeginMemoryRun(_sessionId);
    }

    private void EndMemoryRun()
    {
        if (IsWorking || IsWarming) { _memoryCloseRequested = true; return; }
        if (_memoryRunId is not { } run || _library is null) return;
        try { _library.EndMemoryRun(_sessionId, run); _memoryRunId = null; _memoryCloseRequested = false; }
        catch (Exception error) { HistoryFailure(error); }
    }

    private void RequestMemoryClose()
    {
        if (_disposed || IsVisible || _work?.HasMemory != true || _library is null || _memoryClosing is not null
            || IsWorking || IsWarming || _background?.BlocksNewWork == true
            ||_background?.PendingOperations.Any(state=>state.Kind==LetItOutChatRuntime.MemoryWorkKind)==true) return;
        try
        {
            if (_library.PendingMemoryRun() is not { } run || string.IsNullOrEmpty(_corePath())) return;
            _ = CloseMemoryRunAsync(run, ApplicationBackgroundOperations.ExitToken);
        }
        catch (Exception error) { HistoryFailure(error); }
    }

    private async Task CloseMemoryRunAsync(LetItOutMemoryRun run, CancellationToken token)
    {
        if (_work is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _memoryClosing = cancellation; Availability();
        try
        {
            await _work.CloseMemoryAsync(new(run.ConversationId, run.Id, _corePath()!, _language()), null, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("let_it_out_memory_close_failed", "LetItOut.Memory", detail: error.GetType().Name);
        }
        finally
        {
            _memoryClosing = null;
            if (!_disposed) { Availability(); RequestWarmup(); RequestMemoryClose(); QueueContextRefresh(); }
        }
    }

    public async Task CloseMemoryForExitAsync()
    {
        if (!SaveConversationForExit()) throw new System.IO.IOException("LetItOut.Library.SaveFailed");
        EndMemoryRun();
        if (_work?.HasMemory != true || _library is null || _memoryClosing is not null || IsWorking || IsWarming
            || _background?.BlocksNewWork == true || string.IsNullOrEmpty(_corePath())
            ||_background?.PendingOperations.Any(state=>state.Kind==LetItOutChatRuntime.MemoryWorkKind)==true) return;
        if (_library.PendingMemoryRun() is not { } run) return;
        // An interrupted close retains originals and a pending run, retried on an explicit reopening.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await CloseMemoryRunAsync(run, deadline.Token);
    }
}
