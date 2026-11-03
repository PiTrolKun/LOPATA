using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>A private conversation with its own local library and shared recovery for unfinished work.</summary>
public sealed partial class LetItOutChatControl : UserControl, IDisposable
{
    private sealed class Entry(string role, string content, bool complete = true)
    {
        public string Role { get; } = role;
        public string Content { get; set; } = content;
        public bool Complete { get; set; } = complete;
        public IReadOnlyList<string>? Attachments { get; init; }
        public DateTimeOffset? RecordedUtc { get; } = DateTimeOffset.UtcNow;
        public Entry(string role, string content, bool complete, DateTimeOffset? recordedUtc) : this(role, content, complete)
        { RecordedUtc = recordedUtc; }
    }

    private Func<string, string> _l;
    private Func<string?> _corePath = () => null;
    private Func<string> _language = () => "ru";
    private LetItOutChatWork? _work;
    private readonly List<Entry> _entries = [];
    private string _sessionId = Guid.NewGuid().ToString("N");
    private string? _turnId;
    private CancellationTokenSource? _generation;
    private readonly TextBlock _title = Label("", 28), _status = Label("", 13);
    private readonly TextBlock _hint = Label("", 12), _empty = Label("", 23);
    private readonly StackPanel _messages = new() { Margin = new(0, 20, 0, 20) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLines = 7,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new(0), Padding = new(12), FontSize = 17 };
    private readonly Button _send, _stop, _new, _back, _home;
    private readonly Grid _editorHost;
    private LetItOutComposerBrowser? _flowInput;
    private TextBox? _partialText;
    private Entry? _partialEntry;
    private string _statusKey = "LetItOut.Ready";
    private string _mode = "Auto";
    private bool _requiresPreparation;
    private BackgroundOperationController? _background;
    public bool IsWorking => _generation is not null;
    public string SessionId => _sessionId;
    public event Action? BackRequested;
    public event Action? HomeRequested;
    public event Action<LetItOutChatCheckpoint>? AnswerCompleted;

    public LetItOutChatControl(Func<string, string> localize)
    {
        _l = localize;
        AutomationProperties.SetAutomationId(this, "LetItOut.Chat");
        var surface = new Grid { Margin = new(12, 0, 12, 0) };
        var root = new Grid { MaxWidth = 1060, Margin = new(12, 0, 12, 0) };
        surface.Children.Add(root);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel();
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        _back = MakeButton("LetItOut.Back", () => BackRequested?.Invoke());
        _home = MakeButton("LetItOut.Home", () => HomeRequested?.Invoke());
        _new = MakeButton("LetItOut.New", NewConversation);
        actions.Children.Add(_back); actions.Children.Add(_home); actions.Children.Add(_new);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        var heading = new StackPanel(); heading.Children.Add(_title); heading.Children.Add(_status); header.Children.Add(heading);
        root.Children.Add(header);
        _scroll.Content = _messages; Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        AutomationProperties.SetAutomationId(_scroll, "LetItOut.History");
        var bottom = new StackPanel(); Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        var composer = new Border { CornerRadius = new(18), BorderThickness = new(1), Padding = new(6) };
        composer.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        composer.SetResourceReference(Border.BackgroundProperty, "HeaderBackgroundBrush");
        var inputGrid = new Grid(); inputGrid.RowDefinitions.Add(new()); inputGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _input.Background = System.Windows.Media.Brushes.Transparent;
        _input.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_input, "LetItOut.Input");
        _input.MinLines = 1; _input.MaxLines = 7; _input.Height = 144;
        _input.Padding = new(12, 12, 62, 4);
        _editorHost = new Grid(); _editorHost.Children.Add(_input); inputGrid.Children.Add(_editorHost);
        var sendRow = new Grid { Margin = new(10, 0, 4, 2) }; Grid.SetRow(sendRow, 1);
        sendRow.ColumnDefinitions.Add(new()); sendRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _stop = MakeButton("LetItOut.Stop", () => { _exportCancel?.Cancel(); _cancelSpeech?.Invoke();_treeFailed|=_treeTask is not null;_treeCancel?.Cancel(); _generation?.Cancel(); CancelWarmup(); _memoryClosing?.Cancel(); _contextTrim?.Cancel(); });
        _stop.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        _stop.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
        _stop.Margin = new(4, 0, 4, 2); surface.Children.Add(_stop);
        _send = MakeButton("LetItOut.Send", () => _ = SendAsync()); _send.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        ConfigureSendIcon();
        _send.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        _send.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
        _send.Margin = new(8, 0, 4, 2); Grid.SetRowSpan(_send, 2);
        _hint.VerticalAlignment = System.Windows.VerticalAlignment.Bottom; _hint.Margin = new(0, 0, 56, 0);
        AutomationProperties.SetAutomationId(_hint, "LetItOut.InputHint"); sendRow.Children.Add(_hint);
        inputGrid.Children.Add(sendRow); inputGrid.Children.Add(_send); composer.Child = inputGrid; bottom.Children.Add(composer);
        BuildAttachmentInput(bottom, composer);
        BuildLibraryLayout(surface, root, actions, heading);
        AutomationProperties.SetAutomationId(_status, "LetItOut.Status");
        Content = surface;
        _input.TextChanged += (_, _) => { TreeInputChanged(); _flowInput?.SetText(_input.Text); QueueConversationSave(); QueueContextRefresh(); Availability(); };
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
            { e.Handled = true; if (!e.IsRepeat && _send.IsEnabled) await SendAsync(); }
        };
        Loaded += (_, _) => { AttachBackground(); QueueMemoryYield();RequestWarmup(); };
        IsVisibleChanged += (_, _) => WarmVisibilityChanged();
        Unloaded += (_, _) => { _exportCancel?.Cancel(); _attachmentCancel?.Cancel(); _cancelSpeech?.Invoke(); CancelWarmup(); SaveConversationForExit(); EndMemoryRun(); RequestMemoryClose(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _cancelSpeech?.Invoke(); };
        Configure(localize);
    }

    public void ConfigureFlowInput()
    {
        if (_flowInput is not null) return;
        _flowInput = new(_input, _send, _hint, text => _input.Text = text, () => { if (_send.IsEnabled) _ = SendAsync(); }, () => _sessionId);
        _flowInput.FilesDropped = paths => _ = AddAttachmentPathsAsync(paths);
        _flowInput.PasteAttachment = () => ClipboardAttachment();
        Grid.SetRowSpan(_flowInput, 2);
        ((Grid)_editorHost.Parent).Children.Add(_flowInput);
        _flowInput.Start();
    }

    public void Configure(Func<string, string> localize)
    {
        _l = localize; _title.Text = _l("LetItOut.Title");
        _hint.Text = _l("LetItOut.InputHint"); _empty.Text = _l("LetItOut.Empty");
        _input.ToolTip = _l("LetItOut.InputPlaceholder"); AutomationProperties.SetName(_input, _l("LetItOut.InputPlaceholder"));
        foreach (var button in new[] { _back, _home, _new, _stop }) button.Content = _l((string)button.Tag);
        _send.ToolTip = _l("LetItOut.Send"); AutomationProperties.SetName(_send, _l("LetItOut.Send"));
        RenderHistory(); RenderAttachmentDraft(); RefreshLibrary(); _relayoutLibrary?.Invoke(); Availability();
    }

    public void ConfigureRuntime(Func<string?> corePath, LetItOutChatWork work, Func<string> language)
    {
        _corePath = corePath; _work = work; _language = language;
        if (System.IO.Path.GetFileName(corePath()) == LetItOutModel.FileName) _requiresPreparation = false;
        AttachBackground(); Availability(); QueueContextRefresh(reset: true);
    }

    public void ShowMode(string mode)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { _mode = mode; Availability(); });
    }

    public Task<LlamaGpuDecision> DecideGpuAsync(LlamaGpuAdmission admission, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        token.ThrowIfCancellationRequested();
        var message = string.Format(_l("LetItOut.Memory.Question"), admission.Device,
            admission.RequiredBytes / (1024d * 1024 * 1024), admission.FreeBytes / (1024d * 1024 * 1024));
        var dialog = new LetItOutMemoryDecisionWindow(Window.GetWindow(this), _l, message);
        BackgroundUserDecision.ShowDialog(dialog, this, token);
        if (dialog.Choice == "cancel") { _generation?.Cancel(); CancelWarmup(); }
        return dialog.Choice switch { "retry" => LlamaGpuDecision.Retry, "hybrid" => LlamaGpuDecision.Hybrid, _ => LlamaGpuDecision.Cancel };
    }).Task;

    private void AttachBackground()
    {
        var current = ApplicationBackgroundOperations.Current;
        if (ReferenceEquals(current, _background)) return;
        if (_background is not null) _background.Changed -= BackgroundChanged;
        _background = current;
        if (_background is not null) _background.Changed += BackgroundChanged;
        Availability();
    }

    private void BackgroundChanged()
    { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { Availability(); RequestWarmup(); RequestMemoryClose(); QueueTree(); if (_contextDirty) _contextTimer?.Start(); }); }

    private void Availability()
    {
        var ownPending = _background?.FindOperations(LetItOutChatWork.Kind, _sessionId).FirstOrDefault();
        var ownWarm = _background?.FindOperations(LetItOutChatWarmup.Kind, _sessionId).FirstOrDefault();
        var blocked = _background?.BlocksNewWork == true && !OwnTreeRunning && !OwnSpeechRunning || ownPending is not null || ownWarm is not null || MemoryIsBlocking;
        _send.IsEnabled = !_importingAttachments && !IsWorking && !IsWarming && !blocked && !_requiresPreparation && !_historySaveFailed && _work is not null && (!string.IsNullOrWhiteSpace(_input.Text) || _draftAttachments.Count > 0);
        _new.IsEnabled = !_importingAttachments && !IsWorking && !IsWarming && !blocked && !_requiresPreparation; _stop.IsEnabled = _exportCancel is not null || SpeechRequested || IsWorking || IsWarming || MemoryIsBlocking || _contextTrim is not null || _treeTask is not null;
        if (_attach is not null) _attach.IsEnabled = !_importingAttachments;
        // The next draft remains editable while the current answer is being produced.
        var state = ownPending ?? ownWarm ?? _background?.State;
        var key = state?.Kind == LetItOutChatWork.Kind && state.Project == _sessionId
            && state.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Pausing or BackgroundOperationPhase.Waiting
            ? "LetItOut.Paused" : !IsWorking && blocked ? "LetItOut.Busy" : _statusKey;
        if (!IsWorking && state?.Kind == LetItOutChatWork.Kind && state.Project == _sessionId
            && state.Phase == BackgroundOperationPhase.Waiting && IsMemoryStatus(state.Detail)) key = state.Detail!;
        RenderStatus(MemoryIsBlocking ? "LetItOut.MemorySaving" : _naming ? "LetItOut.Library.Naming" : WarmupStatus(state) ?? key);
        ContextInfoChanged?.Invoke();
    }

    private async Task SendAsync()
    {
        if (!_send.IsEnabled || _work is null || IsWorking) return;
        if (_stopSpeech is not null) await _stopSpeech();
        _treeForeground=true;
        try{await SuspendTreeAsync();}
        finally{_treeForeground=false;}
        var path = _corePath();
        if (string.IsNullOrWhiteSpace(path)) { _statusKey = "LetItOut.NotPrepared"; Availability(); return; }
        var text = _input.Text.Trim(); if (text.Length == 0 && _draftAttachments.Count == 0) return;
        if (!await PrepareAudioAttachmentsAsync()) return;
        if (_draftAttachments.Concat(_entries.SelectMany(e => e.Attachments ?? [])).Any(id => _library?.Attachment(_sessionId, id).Kind == "image"))
        {
            _importingAttachments = true; Availability();
            try { if (_prepareImages is null || !await _prepareImages(ApplicationBackgroundOperations.ExitToken)) return; }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { AttachmentError(error); return; }
            finally { _importingAttachments = false; Availability(); }
        }
        if (!SaveConversationForExit()) return;
        EnsureMemoryRun();
        var attachments = _draftAttachments.ToArray();
        _entries.Add(new("user", text) { Attachments = attachments }); _input.Clear(); _draftAttachments.Clear(); RenderAttachmentDraft();
        if (!SaveConversationForExit()) { _entries.RemoveAt(_entries.Count - 1); _input.Text = text; _draftAttachments.AddRange(attachments); RenderAttachmentDraft(); return; }
        var request = new LetItOutChatRequest(_sessionId, Guid.NewGuid().ToString("N"), path,
            _entries.Where(e => e.Role == "user" || e.Complete).Select(e => new LetItOutChatMessage(e.Role, e.Content, e.Attachments)).ToArray(), _language());
        await RunAsync(request, null, ApplicationBackgroundOperations.ExitToken);
    }

    public async Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (IsWorking) return;
        var request = LetItOutChatWork.Snapshot(state.Input.Deserialize<LetItOutChatRequest>()
            ?? throw new InvalidOperationException("Missing chat input."));
        var checkpoint = state.Checkpoint?.Deserialize<LetItOutChatCheckpoint>();
        AdoptConversation(request.SessionId); _sessionId = request.SessionId; _entries.Clear();
        foreach (var message in request.Messages) _entries.Add(new(message.Role, message.Content, true, null) { Attachments = message.Attachments });
        await RunAsync(request, state, token, new LlamaAnswerContentFilter().Append(checkpoint?.Partial ?? ""));
    }

    public void ShowPending(BackgroundOperationState state)
    {
        if (IsWorking) return;
        var request = LetItOutChatWork.Snapshot(state.Input.Deserialize<LetItOutChatRequest>()
            ?? throw new InvalidOperationException("Missing chat input."));
        var checkpoint = state.Checkpoint?.Deserialize<LetItOutChatCheckpoint>();
        AdoptConversation(request.SessionId); _sessionId = request.SessionId; _entries.Clear();
        foreach (var message in request.Messages) _entries.Add(new(message.Role, message.Content, true, null) { Attachments = message.Attachments });
        if (checkpoint is { Completed: true, Result.Length: > 0 })
            _entries.Add(new("assistant", LetItOutChatRuntime.CleanAnswer(checkpoint.Result), true, null));
        else if (!string.IsNullOrEmpty(checkpoint?.Partial))
            _entries.Add(new("assistant", new LlamaAnswerContentFilter().Append(checkpoint.Partial), false, null));
        _statusKey = "LetItOut.Paused"; RenderHistory(); Availability();
    }

    private async Task RunAsync(LetItOutChatRequest request, BackgroundOperationState? restored, CancellationToken token, string partial = "")
    {
        if (_work is null) throw new InvalidOperationException("Chat runtime is not configured.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        _generation = cancel; _turnId = request.TurnId; _statusKey = "LetItOut.Working";
        var entry = new Entry("assistant", partial, false, restored is null ? DateTimeOffset.UtcNow : null); _entries.Add(entry); _partialEntry = entry;
        RenderHistory(); Availability(); _scroll.ScrollToEnd();
        try
        {
            var progress = new InlineProgress<string>(value => Dispatcher.Invoke(() =>
            {
                if (_turnId != request.TurnId || _generation != cancel) return;
                var follow = _scroll.ScrollableHeight - _scroll.VerticalOffset < 80;
                entry.Content = value; if (_partialText is not null) _partialText.Text = value;
                QueueConversationSave();
                if (follow) _scroll.ScrollToEnd();
            }));
            var result = await _work.GenerateAsync(request, progress, cancel.Token, restored, async (answer, attempt) =>
            {
                entry.Content = answer; entry.Complete = true;
                if (!SaveConversationForExit()) throw new System.IO.IOException("LetItOut.Library.SaveFailed");
                RenderHistory();
                await NameConversationAsync(request, answer, attempt);
                await _work.SummarizePairAsync(request, attempt);
            });
            entry.Content = result; entry.Complete = true; _statusKey = "LetItOut.Ready";
            AnswerCompleted?.Invoke(new(request, result, result, true));
        }
        catch (OperationCanceledException) { _statusKey = "LetItOut.Stopped"; }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _statusKey = error is LetItOutChatContextException || error.Message == "LetItOut.ContextFull"
                ? "LetItOut.ContextFull" : "LetItOut.Failed";
            if (error.Message == "LetItOut.ModelChanged") { _statusKey = error.Message; _requiresPreparation = true; }
            if (IsMemoryStatus(error.Message)) _statusKey = error.Message;
            if (error.Message.StartsWith("LetItOut.Attachments.", StringComparison.Ordinal)) _statusKey = error.Message;
        }
        finally
        {
            _generation = null; _turnId = null; _partialEntry = null;
            SaveConversationForExit(); RefreshLibrary(); RenderHistory(); Availability();
            if (_memoryCloseRequested) EndMemoryRun();
            RequestMemoryClose();
            QueueContextRefresh();
            QueueTree();
            if (entry.Complete && _statusKey == "LetItOut.Ready" && _readSpeech is not null && _autoSpeech() && IsVisible)
                _ = _readSpeech(SpeechMessage(entry), true);
        }
    }

    private void NewConversation()
    {
        if (IsWorking || !_new.IsEnabled) return;
        if (!SaveConversationForExit()) return;
        EndMemoryRun(); ResetConversation(); RequestMemoryClose();
        if (_flowInput is not null) _flowInput.FocusEditor(); else _input.Focus();
    }

    private static bool IsMemoryStatus(string? key) => key is "LetItOut.RamRequired" or "LetItOut.VramChanged" or "LetItOut.Preparation.EngineRequired";

    private void RenderHistory()
    {
        _messages.Children.Clear(); _partialText = null; _speechButtons.Clear();
        if (_entries.Count == 0)
        {
            _empty.Margin = new(16, 60, 16, 25); _empty.TextAlignment = TextAlignment.Center;
            _messages.Children.Add(_empty); return;
        }
        foreach (var entry in _entries)
        {
            var user = entry.Role == "user";
            var card = new Border { CornerRadius = new(16), Padding = new(14, 8, 14, 10), Margin = new(user ? 90 : 0, 6, user ? 0 : 60, 8) };
            if (user) card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            var body = new StackPanel();
            var label = Label(_l(user ? "LetItOut.You" : entry.Complete ? "LetItOut.Assistant" : ReferenceEquals(entry, _partialEntry) && IsWorking ? "LetItOut.Assistant" : "LetItOut.Incomplete"), 12);
            body.Children.Add(label);
            var text = new TextBox { Text = entry.Content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new(0, 4, 0, 4), FontSize = 17 };
            text.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetAutomationId(text, user ? "LetItOut.UserMessage" : "LetItOut.AssistantMessage");
            RenderMessageAttachments(entry, body); body.Children.Add(text); AddSpeechButton(entry, body); RenderRecallSources(entry, body); card.Child = body; _messages.Children.Add(card);
            if (ReferenceEquals(entry, _partialEntry)) _partialText = text;
        }
    }

    private Button MakeButton(string key, Action action)
    {
        var button = new Button { Tag = key, Content = _l(key), Margin = new(4), Padding = new(14, 7, 14, 7), MinWidth = 80 };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => action(); return button;
    }

    private static TextBlock Label(string text, double size)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new(2, 4, 2, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, size <= 13 ? "TextSecondaryBrush" : "TextPrimaryBrush"); return label;
    }

    public void Dispose()
    {
        _exportCancel?.Cancel(); _attachmentCancel?.Cancel();_cancelSpeech?.Invoke();_treeTimer?.Stop();_treeCancel?.Cancel();SaveConversationForExit(); EndMemoryRun(); _disposed = true; DisposeContextIndicator(); CancelWarmup(); _titleCancellation?.Cancel(); _flowInput?.Dispose();
        _generation?.Cancel();
        if (_background is not null) _background.Changed -= BackgroundChanged;
    }
}
