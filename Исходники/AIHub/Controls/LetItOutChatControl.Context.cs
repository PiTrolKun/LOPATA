using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private DispatcherTimer? _contextTimer;
    private CancellationTokenSource? _contextRead, _contextTrim;
    private long _contextRevision;
    private bool _contextDirty;
    public LetItOutContextInfo? ContextInfo { get; private set; }
    public event Action? ContextInfoChanged;
    public bool CanTrimContext => ContextInfo?.CanTrim == true && IsVisible && !_disposed
        && !IsWorking && !IsWarming && !MemoryIsBlocking && _contextTrim is null
        && _background?.BlocksNewWork != true;

    private LetItOutChatRequest ContextRequest()
    {
        var messages = _entries.Where(e => e.Role == "user" || e.Complete)
            .Select(e => new LetItOutChatMessage(e.Role, e.Content, e.Attachments)).ToList();
        if (!string.IsNullOrWhiteSpace(_input.Text) || _draftAttachments.Count > 0) messages.Add(new("user", _input.Text, _draftAttachments.ToArray()));
        return new(_sessionId, Guid.NewGuid().ToString("N"), _corePath()!, messages, _language());
    }

    internal void AcceptContextSnapshot(string id, LetItOutContextInfo info)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AcceptContextSnapshot(id, info)); return; }
        if (_disposed || id != _sessionId) return;
        ContextInfo = info; ContextInfoChanged?.Invoke();
    }

    private void QueueContextRefresh(bool reset = false)
    {
        _contextRead?.Cancel(); _contextRevision++; _contextDirty = true;
        if (reset) { ContextInfo = null; ContextInfoChanged?.Invoke(); }
        if (_disposed) return;
        _contextTimer ??= CreateContextTimer(); _contextTimer.Stop();
        if (IsVisible && _work?.HasContext == true) _contextTimer.Start();
    }

    private DispatcherTimer CreateContextTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); _ = RefreshContextAsync(); };
        return timer;
    }

    private async Task RefreshContextAsync()
    {
        if (!_contextDirty || _disposed || !IsVisible || _work?.HasContext != true
            || IsWorking || IsWarming || MemoryIsBlocking || _contextTrim is not null
            || _background?.BlocksNewWork == true || string.IsNullOrEmpty(_corePath())) return;
        if (!SaveConversationForExit()) return;
        // Counting an empty topic must not create it in the library.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(10));
        var revision = _contextRevision; var request = ContextRequest(); _contextRead = cancel; _contextDirty = false;
        try
        {
            var info = await _work.MeasureContextAsync(request, cancel.Token);
            if (!cancel.IsCancellationRequested && revision == _contextRevision) AcceptContextSnapshot(request.SessionId, info);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision == _contextRevision) { ContextInfo = null; ContextInfoChanged?.Invoke(); }
            OwnedProcessRegistry.Log("let_it_out_context_unavailable", "LetItOut.Context", detail: error.GetType().Name);
        }
        finally { if (ReferenceEquals(_contextRead, cancel)) _contextRead = null; }
    }

    public async Task TrimContextAsync()
    {
        if (!CanTrimContext || _work?.HasContext != true || !SaveConversationForExit()) return;
        QueueContextRefresh(); _contextTimer?.Stop();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        cancel.CancelAfter(TimeSpan.FromSeconds(15)); _contextTrim = cancel;
        var request = ContextRequest();
        try
        {
            using var lease = _background?.BeginAuxiliary();
            Availability(); ContextInfoChanged?.Invoke();
            var info = await _work.TrimContextAsync(request, cancel.Token);
            if (!cancel.IsCancellationRequested) AcceptContextSnapshot(request.SessionId, info);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("let_it_out_context_trim_failed", "LetItOut.Context", detail: error.GetType().Name);
            _statusKey = "LetItOut.Context.TrimFailed";
        }
        finally
        {
            _contextTrim = null; Availability(); ContextInfoChanged?.Invoke(); QueueContextRefresh();
        }
    }

    private void DisposeContextIndicator()
    { _contextTimer?.Stop(); _contextRead?.Cancel(); _contextTrim?.Cancel(); ContextInfo = null; ContextInfoChanged?.Invoke(); }
}
