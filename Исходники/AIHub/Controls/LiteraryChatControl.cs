using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryChatControl : UserControl
{
    private Func<string, string> _l;
    private readonly LiteraryChatRuntime _runtime;
    private readonly LiteraryChatProfile _profile;
    private readonly Func<string> _draft;
    private readonly Func<LiteraryEditorSnapshot>? _snapshot;
    private readonly LiteraryProject _project;
    private readonly List<ImageAnalysisHiddenMessage> _history = [];
    private readonly LiteraryTranscript _transcript = new();
    private readonly TextBox _input = LiteraryWorkspaceParts.TextArea(false);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _send = new(), _clear = new();
    private CancellationTokenSource? _cts;
    private string _statusKey = "Literary.Writer.Ready";
    private readonly List<(bool User, string Text)> _display = [];
    private readonly LiteraryDialogueStore? _dialogStore;
    private LiteraryDialogue? _dialog;
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LiteraryStreamDisplay? _currentStream;
    public bool ActionsBlocked { get; set; }
    public void RefreshAvailability() => UpdateButtons();
    public bool IsInputOrigin(object origin) => origin is System.Windows.DependencyObject value && (value == _input || _input.IsAncestorOf(value));
    public LiteraryChatControl(Func<string, string> localize, LiteraryChatRuntime runtime,
        LiteraryChatProfile profile, Func<string> draft, LiteraryProject project, Func<LiteraryEditorSnapshot>? snapshot = null, string? directory = null)
    {
        _profile = profile;
        _runtime = runtime; _draft = draft; _project = project; _snapshot = snapshot;
        _l = LocalizeRole(localize);
        if (directory is not null)
        {
            _dialogStore = new(new LiteraryProjectLayout(directory), profile);
            _dialog = _dialogStore.Load();
            foreach (var message in _dialog.Messages)
            {
                _display.Add((message.User, message.Text + (!message.Complete ? "\n[" + _l("Literary.Writer.Incomplete") + "]" : "")));
                if (profile == LiteraryChatProfile.Advisor && message.Complete)
                    _history.Add(new() { Role = message.User ? "user" : "assistant", Content = message.Text });
            }
            _input.Text = _dialog.Input;
            _transcript.ShowHistory(_display, _l("Literary.Writer.You"), _l("Literary.Workspace.Writer"));
        }
        _send.Click += async (_, _) => { if (_cts is not null) _cts.Cancel(); else await SendAsync(); };
        _clear.Click += (_, _) => { if (ActionsBlocked) return; _history.Clear(); _display.Clear(); _transcript.Clear(); if (_dialog is not null) { _dialog.Id = Guid.NewGuid().ToString("N"); _dialog.Messages.Clear(); } SaveDialogue(); _runtime.ResetAnchor(_profile); SetStatus("Literary.Writer.Ready"); UpdateButtons(); };
        _runtime.BusyChanged += () => Dispatcher.BeginInvoke(new Action(UpdateButtons));
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            { e.Handled = true; if (_cts is null) await SendAsync(); }
        };
        _input.TextChanged += (_, _) => UpdateButtons();
        Unloaded += (_, _) => _cts?.Cancel();
        _saveTimer.Tick += (_, _) => SaveDialogue();
        Loaded += (_, _) => _saveTimer.Start();
        Unloaded += (_, _) => { SaveDialogue(); _saveTimer.Stop(); };
        Render();
    }
    public bool SaveDialogue()
    {
        if (_dialogStore is null || _dialog is null) return true;
        try
        {
            _dialog.Input = _input.Text;
            _dialog.Partial = _currentStream?.Snapshot() ?? "";
            _dialog.Generating = _cts is not null;
            _dialogStore.Save(_dialog); return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { SetStatus("Literary.Dialog.SaveError"); return false; }
    }
    public void ApplyLocalization(Func<string, string> localize) { _l = LocalizeRole(localize); Render(); }
    private Func<string, string> LocalizeRole(Func<string, string> localize) => key => localize(
        _profile == LiteraryChatProfile.Advisor ? key switch
        {
            "Literary.Workspace.Writer" => "Literary.Workspace.Advisor",
            "Literary.Writer.Ready" => "Literary.Advisor.Ready",
            "Literary.Writer.Working" => "Literary.Advisor.Working",
            "Literary.Writer.Error" => "Literary.Advisor.Error",
            "Literary.Writer.Temporary" => "Literary.Advisor.Temporary",
            _ => key
        } : key);
    private void Render()
    {
        // Reuse the active input/history controls when the application language changes.
        foreach (var element in new FrameworkElement[] { _transcript, _input, _status, _send, _clear })
            if (element.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(element);
        var panel = new DockPanel();
        var heading = new WrapPanel();
        heading.Children.Add(LiteraryUi.Text(_l("Literary.Workspace.Writer"), true));
        _clear.Content = _l("Literary.Writer.Clear");
        _clear.Margin = new Thickness(8, 0, 0, 0); _clear.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); heading.Children.Add(_clear);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var notice = LiteraryUi.Text(_l("Literary.Writer.Temporary")); DockPanel.SetDock(notice, Dock.Top); panel.Children.Add(notice);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _status.Text = _l(_statusKey); _status.Margin = new Thickness(0, 6, 0, 6);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        var inputRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _send.SetResourceReference(StyleProperty, "PrimaryButtonStyle"); _send.MinWidth = 0; _send.Width = 38; _send.Padding = new Thickness(4); _send.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_send, Dock.Right); inputRow.Children.Add(_send);
        _input.Height = 68; _input.MaxLength = 0; _input.ToolTip = _l("Literary.Writer.InputHint"); inputRow.Children.Add(_input);
        DockPanel.SetDock(inputRow, Dock.Bottom); panel.Children.Add(inputRow);
        panel.Children.Add(_transcript);
        Content = LiteraryWorkspaceParts.Card(panel);
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        _send.Content = _cts is null ? "➤" : "■";
        _send.ToolTip = _l(_cts is null ? "Literary.Writer.Send" : "Literary.Writer.Stop");
        System.Windows.Automation.AutomationProperties.SetName(_send, (string)_send.ToolTip);
        _send.IsEnabled = !ActionsBlocked && (_cts is not null || (!_runtime.IsBusy && !string.IsNullOrWhiteSpace(_input.Text)));
        _send.Opacity = _send.IsEnabled ? 1 : 0.45;
        _input.IsReadOnly = _cts is not null; _clear.IsEnabled = !ActionsBlocked && !_runtime.IsBusy && _cts is null && _display.Count > 0;
        _status.Text = _l(_runtime.IsBusy && _cts is null ? "Literary.Shared.Waiting" : _statusKey);
    }
    private void SetStatus(string key) { _statusKey = key; _status.Text = _l(key); }
    private void BeginReply() => _transcript.BeginReply(_display, _l("Literary.Writer.You"), _l("Literary.Workspace.Writer"));
    private async Task SendAsync()
    {
        var text = _input.Text; if (ActionsBlocked || string.IsNullOrWhiteSpace(text) || _cts is not null || _runtime.IsBusy) return;
        var request = _history.Concat([new ImageAnalysisHiddenMessage { Role = "user", Content = text }]).ToArray();
        using var cts = new CancellationTokenSource(); _cts = cts;
        _display.Add((true, text)); _input.Clear();
        _dialog?.Messages.Add(new(true, text));
        BeginReply();
        using var streamDisplay = new LiteraryStreamDisplay(_transcript);
        _currentStream = streamDisplay;
        if (!SaveDialogue()) { _cts = null; _currentStream = null; _input.Text = text; UpdateButtons(); return; }
        var recovered = false;
        var limited = false;
        var finished = false;
        SetStatus("Literary.Writer.Working"); UpdateButtons();
        try
        {
            var snapshot = _snapshot?.Invoke();
            var result = await _runtime.SendAsync(_profile, request, snapshot?.Text ?? _draft(), _project, streamDisplay, cts.Token,
                async () => await Dispatcher.InvokeAsync(() =>
                {
                    PreservePartial();
                    recovered = true;
                    streamDisplay.Reset(BeginReply);
                    _currentStream = streamDisplay;
                    SetStatus("Literary.Loop.Retrying");
                }), snapshot, key =>
                {
                    if (key == "Literary.Context.Limited") limited = true;
                    Dispatcher.BeginInvoke(new Action(() => { if (_cts == cts && !finished) SetStatus(key); }));
                });
            finished = true;
            if (_profile == LiteraryChatProfile.Advisor)
            { _history.Add(request[^1]); _history.Add(new ImageAnalysisHiddenMessage { Role = "assistant", Content = result }); }
            _display.Add((false, result));
            _dialog?.Messages.Add(new(false, result));
            var changed = false;
            if (snapshot is not null)
            {
                try { changed = _snapshot!().Revision != snapshot.Revision; }
                catch (System.IO.IOException) { changed = true; }
            }
            SetStatus(changed ? "Literary.Context.Changed" : limited ? "Literary.Context.Limited" : recovered ? "Literary.Loop.Recovered" : "Literary.Writer.Ready");
        }
        catch (LiterarySourceException) { PreservePartial(); SetStatus("Literary.Context.SourceError"); _input.Text = text; }
        catch (LiteraryLoopException) { PreservePartial(); SetStatus("Literary.Loop.Stopped"); _input.Text = text; }
        catch (LiteraryDraftLimitException) { PreservePartial(); SetStatus("Literary.Draft.TokenLimit"); _input.Text = text; }
        catch (ImageAnalysisContextExhaustedException ex) { PreservePartial(); SetStatus(ex.OutputTruncated ? "Literary.Shared.OutputLimit" : "Literary.Writer.Context"); _input.Text = text; }
        catch (OperationCanceledException) { PreservePartial(); SetStatus(cts.IsCancellationRequested ? "Literary.Writer.Cancelled" : "Literary.Writer.Timeout"); _input.Text = text; }
        catch (System.IO.FileNotFoundException) { PreservePartial(); SetStatus("Literary.Writer.Missing"); _input.Text = text; }
        catch (Exception) { PreservePartial(); SetStatus("Literary.Writer.Error"); _input.Text = text; }
        finally { finished = true; await streamDisplay.CompleteAsync(); _cts = null; _currentStream = null; SaveDialogue(); UpdateButtons(); }
        void PreservePartial()
        {
            var partial = streamDisplay.Snapshot();
            _currentStream = null;
            if (partial.Length > 0)
            {
                var marker = "\n[" + _l("Literary.Writer.Incomplete") + "]";
                _display.Add((false, partial + marker));
                _dialog?.Messages.Add(new(false, partial, false));
                streamDisplay.Report(new ModelStreamChunk(marker));
            }
        }
    }
}
