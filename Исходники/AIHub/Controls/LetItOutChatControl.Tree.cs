using System.Windows;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button=System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private bool _treeEnabled,_treeFailed,_treeForeground;
    private CancellationTokenSource? _treeCancel;
    private Task? _treeTask;
    private DispatcherTimer? _treeTimer;
    private Button _treeToggle=null!;
    private readonly Dictionary<string,LetItOutTopicMapWindow> _treeWindows=[];
    private bool OwnTreeRunning=>_treeTask is not null&&_background?.State is {Kind:LetItOutChatRuntime.TreeWorkKind} state
        &&state.Project==_sessionId&&!state.UserPaused;
    private void InitializeTree()
    {
        _tree.OpenRequested+=OpenTreeMap;
        _tree.RetryRequested+=()=>{_treeFailed=false;QueueTree();};
        _treeTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(4)};
        _treeTimer.Tick+=(_,_)=>{_treeTimer.Stop();TryBuildTree();};
        Loaded+=(_,_)=>QueueTree();
        IsVisibleChanged+=(_,_)=>{if(IsVisible)QueueTree();else _treeTimer.Stop();};
        Unloaded+=(_,_)=>{_treeTimer.Stop();_treeCancel?.Cancel();};
    }
    private void RefreshTree()
    {
        var state=_library is not null&&_persisted?_library.Tree(_sessionId):new LetItOutTreeState(_treeEnabled,null);
        _treeEnabled=state.Enabled;
        _tree.Update(state.Document,_l,_treeTask is not null,_treeFailed);
        if(state.Document is{} document&&_treeWindows.TryGetValue(_sessionId,out var window)&&window.Revision!=document.Revision)
            window.Update(document,_library!.MemoryPairs(_sessionId));
        if(_treeToggle is null)return;
        _treeToggle.Content=_treeEnabled?">":"<";
        var label=_l(_treeEnabled?"LetItOut.Tree.Collapse":"LetItOut.Tree.Expand");
        _treeToggle.ToolTip=label;AutomationProperties.SetName(_treeToggle,label);
        _relayoutLibrary?.Invoke();
    }
    private void ToggleTree()
    {
        try
        {
            _treeEnabled=!_treeEnabled;
            if(_library is not null)
            {
                if(!_persisted){_library.Create(_sessionId);_persisted=true;SaveConversationForExit();}
                _library.SetTreeEnabled(_sessionId,_treeEnabled);
            }
            if(!_treeEnabled)
            {
                _treeTimer?.Stop();_treeCancel?.Cancel();
                foreach(var operation in _background?.FindOperations(LetItOutChatRuntime.TreeWorkKind,_sessionId)??[])
                    if(!_background!.HasLiveOperation(operation.Id))_background.DiscardPending(operation.Id);
            }
            else {_treeFailed=false;QueueTree();}
            RefreshTree();RefreshLibrary();
        }
        catch(Exception error){HistoryFailure(error);}
    }
    private void QueueTree()
    {
        if(_disposed||!IsVisible||!_treeEnabled||_treeFailed||_treeForeground)return;
        _treeTimer?.Stop();_treeTimer?.Start();
    }
    private void TryBuildTree()
    {
        if(_disposed||!IsVisible||!_treeEnabled||_treeFailed||_treeForeground||SpeechRequested||_treeTask is not null
            ||_work?.HasTree!=true||_library is null||!_persisted||!string.IsNullOrWhiteSpace(_input.Text)
            ||string.IsNullOrWhiteSpace(_corePath()))return;
        if(IsWorking||IsWarming||MemoryIsBlocking||_contextTrim is not null||_contextRead is not null||_background?.BlocksNewWork==true)
        {QueueTree();return;}
        if(_background?.FindOperations(LetItOutChatRuntime.TreeWorkKind,_sessionId).Any()==true)return;
        var saved=_library.Tree(_sessionId);var count=_library.MemoryPairs(_sessionId).Count;
        if(!saved.Enabled||count-(saved.Document?.Through??0)<1||count<2)return;
        _treeTask=RunTreeAsync(new(_sessionId,_corePath()!,_language()),null,ApplicationBackgroundOperations.ExitToken);
    }
    private async Task RunTreeAsync(LetItOutTreeRequest request,BackgroundOperationState? restored,CancellationToken token)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(token,ApplicationBackgroundOperations.ExitToken);
        _treeCancel=cancel;
        try
        {
            // Yield so the task/priority flag is installed before the shared controller reports its state.
            await Dispatcher.Yield(DispatcherPriority.Background);RefreshTree();Availability();
            await _work!.UpdateTreeAsync(request,restored,cancel.Token);
        }
        catch(OperationCanceledException){if(!cancel.IsCancellationRequested)_treeFailed=true;}
        catch(Exception error)
        {
            _treeFailed=true;ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("let_it_out_tree_failed","LetItOut.Tree",detail:error.GetType().Name);
        }
        finally
        {
            _treeCancel=null;_treeTask=null;
            if(!_disposed){RefreshTree();Availability();QueueTree();QueueContextRefresh();}
        }
    }
    private async Task SuspendTreeAsync()
    {
        _treeTimer?.Stop();_treeCancel?.Cancel();
        if(_treeTask is{} task)await task;
    }
    private void TreeInputChanged()
    {
        if(!string.IsNullOrWhiteSpace(_input.Text))QueueMemoryYield();
        if(!string.IsNullOrWhiteSpace(_input.Text)){_treeTimer?.Stop();_treeCancel?.Cancel();}
        else QueueTree();
    }
    public Task ResumeTreeAsync(BackgroundOperationState state,CancellationToken token)
    {
        var request=state.Input.Deserialize<LetItOutTreeRequest>()??throw new InvalidOperationException("LetItOut.InvalidCheckpoint");
        if(_library?.IsDeleted(request.ConversationId)==true)return Task.CompletedTask;
        AdoptConversation(request.ConversationId);RefreshLibrary();
        return _treeTask=RunTreeAsync(request,state,token);
    }
    private void OpenTreeMap(string? focus)
    {
        if(_library?.Tree(_sessionId).Document is not{} map)return;
        if(_treeWindows.TryGetValue(_sessionId,out var previous)){previous.FocusNode(focus);return;}
        var id=_sessionId;
        var window=new LetItOutTopicMapWindow(map,_library.MemoryPairs(_sessionId),_l,focus,Window.GetWindow(this));
        _treeWindows[id]=window;window.Closed+=(_,_)=>_treeWindows.Remove(id);
        window.Show();
    }
}
