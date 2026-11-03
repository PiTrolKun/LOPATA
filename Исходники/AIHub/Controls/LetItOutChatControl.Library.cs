using System.Windows;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private LetItOutConversationStore? _library;
    private LetItOutConversationSummary? _topic;
    private bool _persisted, _loadingTopic, _historySaveFailed;
    private DispatcherTimer? _saveTimer;
    private CancellationTokenSource? _titleCancellation;
    private bool _naming;

    public void ConfigureHistory(LetItOutConversationStore store)
    {
        if (_library is not null) return;
        _library = store;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => SaveConversationForExit();
        _topicCaption.Visibility = Visibility.Visible;
        var recent = store.List().FirstOrDefault();
        if (_entries.Count == 0 && string.IsNullOrEmpty(_input.Text) && recent is not null) OpenConversation(recent.Id);
        else { SaveConversationForExit(); RefreshLibrary(); }
        _relayoutLibrary?.Invoke();
    }

    private void QueueConversationSave()
    {
        if (_library is null || _loadingTopic || _disposed) return;
        _saveTimer?.Stop(); _saveTimer?.Start();
    }

    public bool SaveConversationForExit()
    {
        _saveTimer?.Stop();
        if (_library is null || _loadingTopic) return true;
        if (!_persisted && _entries.Count == 0 && string.IsNullOrEmpty(_input.Text) && _draftAttachments.Count == 0) return true;
        try
        {
            var created = !_persisted;
            if (!_persisted) { _library.Create(_sessionId); _persisted = true; }
            _library.SaveContent(_sessionId, _entries.Select(entry => new LetItOutSavedMessage(entry.Role, entry.Content, entry.Complete, entry.RecordedUtc, entry.Attachments)).ToArray(), _input.Text);
            SaveAttachmentDraft();
            _historySaveFailed = false;
            if (_statusKey == "LetItOut.Library.SaveFailed") _statusKey = "LetItOut.Ready";
            if (created) RefreshLibrary();
            return true;
        }
        catch (Exception error) { HistoryFailure(error); return false; }
    }

    private void HistoryFailure(Exception error)
    {
        _historySaveFailed = true; _statusKey = "LetItOut.Library.SaveFailed";
        OwnedProcessRegistry.Log("conversation_store_failed", "LetItOut.History", detail: error.GetType().Name);
        Availability();
    }

    private void RefreshLibrary()
    {
        if (_library is null) return;
        try
        {
            var items = _library.List(); _topic = items.FirstOrDefault(topic => topic.Id == _sessionId);
            _sidebar.Update(items, _sessionId, _l);
            _topicCaption.Text = TopicName(_topic); RefreshTree();QueueTree();
            _topicsButton.ToolTip = _l("LetItOut.Library.Topics");
            System.Windows.Automation.AutomationProperties.SetName(_topicsButton, _l("LetItOut.Library.Topics"));
        }
        catch (Exception error) { HistoryFailure(error); }
    }
    private string TopicName(LetItOutConversationSummary? topic) => string.IsNullOrWhiteSpace(topic?.Title)
        ? _l("LetItOut.Library.Untitled") : topic.Title;

    internal bool OpenConversation(string id)
    {
        if (_library is null || IsWorking || IsWarming || !SaveConversationForExit()) return false;
        try
        {
            var conversation = _library.Load(id); if (conversation is null) return false;
            _cancelSpeech?.Invoke();
            _treeCancel?.Cancel();_treeFailed=false;
            if (id != _sessionId) EndMemoryRun();
            _loadingTopic = true;
            try
            {
                _sessionId = id; _persisted = true; _topic = conversation.Summary; _entries.Clear();
                foreach (var message in conversation.Messages) _entries.Add(new(message.Role, message.Content, message.Complete, message.RecordedUtc) { Attachments = message.Attachments });
                RestoreAttachmentDraft(conversation);
                _input.Text = conversation.Draft; _statusKey = "LetItOut.Ready";
            }
            finally { _loadingTopic = false; }
            RenderHistory(); RefreshLibrary(); Availability(); QueueContextRefresh(reset: true); _scroll.ScrollToEnd(); RequestMemoryClose(); return true;
        }
        catch (Exception error) { HistoryFailure(error); return false; }
    }

    private void FavoriteConversation(string id, bool favorite)
    {
        try { _library?.SetFavorite(id, favorite); RefreshLibrary(); }
        catch (Exception error) { HistoryFailure(error); }
    }
    private void RenameConversation(string id)
    {
        if (_library is null) return;
        try
        {
            var topic = _library.Load(id); if (topic is null) return;
            var dialog = new LetItOutRenameWindow(Window.GetWindow(this), _l, TopicName(topic.Summary));
            if (dialog.ShowDialog() == true) SetConversationTitle(id, dialog.TopicTitle);
        }
        catch (Exception error) { HistoryFailure(error); }
    }
    internal void SetConversationTitle(string id, string title)
    {
        if (_library is null) return;
        try
        {
            if (_library.SetManualTitle(id, title) && id == _sessionId) _titleCancellation?.Cancel();
            RefreshLibrary();
        }
        catch (Exception error) { HistoryFailure(error); }
    }
    private void DeleteConversation(string id)
    {
        if (_library is null || id == _sessionId && (IsWorking || IsWarming)) return;
        try
        {
            var topic = _library.Load(id); if (topic is null) return;
            if (System.Windows.MessageBox.Show(Window.GetWindow(this), string.Format(_l("LetItOut.Library.DeleteQuestion"), TopicName(topic.Summary)),
                _l("LetItOut.Library.Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var pending = _background?.PendingOperations.Where(operation => operation.Project == id
                && operation.Kind is LetItOutChatWork.Kind or LetItOutChatWarmup.Kind or LetItOutChatRuntime.MemoryWorkKind or LetItOutChatRuntime.TreeWorkKind or LetItOutConversationExport.Kind).ToArray() ?? [];
            if (pending.Any(operation => _background!.HasLiveOperation(operation.Id))) return;
            foreach (var operation in pending) _background!.DiscardPending(operation.Id);
            if (pending.Any(operation => _background!.PendingOperations.Any(saved => saved.Id == operation.Id))) return;
            if (!_library.Delete(id)) return;
            if(_treeWindows.TryGetValue(id,out var map))map.Close();
            if (id == _sessionId) { _memoryRunId = null; ResetConversation(); }
            RefreshLibrary();
        }
        catch (Exception error) { HistoryFailure(error); }
    }

    private void ResetConversation()
    {
        _cancelSpeech?.Invoke();
        _treeCancel?.Cancel();_treeEnabled=false;_treeFailed=false;
        _loadingTopic = true;
        try { _sessionId = Guid.NewGuid().ToString("N"); _persisted = false; _topic = null; _entries.Clear(); _input.Clear(); RestoreAttachmentDraft(null); }
        finally { _loadingTopic = false; }
        _historySaveFailed = false; _statusKey = "LetItOut.Ready"; RenderHistory(); RefreshLibrary(); Availability(); QueueContextRefresh(reset: true);
    }

    private void AdoptConversation(string id)
    {
        if (_library is null || id == _sessionId) return;
        if (_library.IsDeleted(id)) throw new System.IO.InvalidDataException("Conversation was removed.");
        if (!SaveConversationForExit()) throw new System.IO.IOException("LetItOut.Library.SaveFailed");
        EndMemoryRun();
        var saved = _library.Load(id); _persisted = saved is not null; _topic = saved?.Summary;
        _loadingTopic = true;
        try
        {
            _sessionId = id; _entries.Clear();
            if (saved is not null) foreach (var message in saved.Messages) _entries.Add(new(message.Role, message.Content, message.Complete, message.RecordedUtc) { Attachments = message.Attachments });
            RestoreAttachmentDraft(saved);
            _input.Text = saved?.Draft ?? "";
        }
        finally { _loadingTopic = false; }
    }

    private async Task NameConversationAsync(LetItOutChatRequest request, string answer, CancellationToken token)
    {
        if (_library is null || _work?.CanGenerateTitle != true || !SaveConversationForExit()) return;
        try { if (!_library.TryBeginAutoTitle(request.SessionId)) return; }
        catch (Exception error) { HistoryFailure(error); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _titleCancellation = cancellation; _naming = true; _statusKey = "LetItOut.Library.Naming"; Availability();
        try
        {
            var title = await _work.GenerateTitleAsync(new(request.SessionId, request.CoreModelPath,
                request.Messages.Append(new("assistant", answer)).ToArray(), request.Language), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _library.TryApplyAutoTitle(request.SessionId, title);
        }
        catch (OperationCanceledException) { await _work.RetireAsync(); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            OwnedProcessRegistry.Log("conversation_title_failed", "LetItOut.Title", detail: error.GetType().Name);
        }
        finally { _titleCancellation = null; _naming = false; RefreshLibrary(); }
    }
}
