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
    private readonly LiteraryProject _project;
    private readonly List<ImageAnalysisHiddenMessage> _history = [];
    private readonly TextBox _transcript = LiteraryWorkspaceParts.TextArea();
    private readonly TextBox _input = LiteraryWorkspaceParts.TextArea(false);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _send = new(), _clear = new();
    private CancellationTokenSource? _cts;
    private string _statusKey = "Literary.Writer.Ready";
    private readonly List<(bool User, string Text)> _display = [];
    public LiteraryChatControl(Func<string, string> localize, LiteraryChatRuntime runtime,
        LiteraryChatProfile profile, Func<string> draft, LiteraryProject project)
    {
        _profile = profile;
        _runtime = runtime; _draft = draft; _project = project;
        _l = LocalizeRole(localize);
        _send.Click += async (_, _) => { if (_cts is not null) _cts.Cancel(); else await SendAsync(); };
        _clear.Click += (_, _) => { _history.Clear(); _display.Clear(); _transcript.Clear(); SetStatus("Literary.Writer.Ready"); UpdateButtons(); };
        _runtime.BusyChanged += () => Dispatcher.BeginInvoke(new Action(UpdateButtons));
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            { e.Handled = true; if (_cts is null) await SendAsync(); }
        };
        _input.TextChanged += (_, _) => UpdateButtons();
        Unloaded += (_, _) => _cts?.Cancel();
        Render();
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
        _send.IsEnabled = _cts is not null || (!_runtime.IsBusy && !string.IsNullOrWhiteSpace(_input.Text));
        _input.IsReadOnly = _cts is not null; _clear.IsEnabled = !_runtime.IsBusy && _cts is null && _display.Count > 0;
        _status.Text = _l(_runtime.IsBusy && _cts is null ? "Literary.Shared.Waiting" : _statusKey);
    }
    private void SetStatus(string key) { _statusKey = key; _status.Text = _l(key); }
    private string Transcript() => string.Join("\n\n", _display.Select(m => _l(m.User ? "Literary.Writer.You" : "Literary.Workspace.Writer") + ":\n" + m.Text));
    private async Task SendAsync()
    {
        var text = _input.Text; if (string.IsNullOrWhiteSpace(text) || _cts is not null || _runtime.IsBusy) return;
        var request = _history.Concat([new ImageAnalysisHiddenMessage { Role = "user", Content = text }]).ToArray();
        using var cts = new CancellationTokenSource(); _cts = cts;
        _display.Add((true, text)); _input.Clear();
        var prefix = Transcript() + "\n\n" + _l("Literary.Workspace.Writer") + ":\n";
        using var streamDisplay = new LiteraryStreamDisplay(_transcript);
        var recovered = false;
        _transcript.Text = prefix; SetStatus("Literary.Writer.Working"); UpdateButtons();
        try
        {
            var result = await _runtime.SendAsync(_profile, request, _draft(), _project, streamDisplay, cts.Token,
                async () => await Dispatcher.InvokeAsync(() =>
                {
                    PreservePartial();
                    recovered = true;
                    streamDisplay.Reset(Transcript() + "\n\n" + _l("Literary.Workspace.Writer") + ":\n");
                    SetStatus("Literary.Loop.Retrying");
                }));
            if (_profile == LiteraryChatProfile.Advisor)
            { _history.Add(request[^1]); _history.Add(new ImageAnalysisHiddenMessage { Role = "assistant", Content = result }); }
            _display.Add((false, result)); SetStatus(recovered ? "Literary.Loop.Recovered" : "Literary.Writer.Ready");
        }
        catch (LiteraryLoopException) { PreservePartial(); SetStatus("Literary.Loop.Stopped"); _input.Text = text; }
        catch (LiteraryDraftLimitException) { PreservePartial(); SetStatus("Literary.Draft.TokenLimit"); _input.Text = text; }
        catch (ImageAnalysisContextExhaustedException ex) { PreservePartial(); SetStatus(ex.OutputTruncated ? "Literary.Shared.OutputLimit" : "Literary.Writer.Context"); _input.Text = text; }
        catch (OperationCanceledException) { PreservePartial(); SetStatus(cts.IsCancellationRequested ? "Literary.Writer.Cancelled" : "Literary.Writer.Timeout"); _input.Text = text; }
        catch (System.IO.FileNotFoundException) { PreservePartial(); SetStatus("Literary.Writer.Missing"); _input.Text = text; }
        catch (Exception) { PreservePartial(); SetStatus("Literary.Writer.Error"); _input.Text = text; }
        finally { await streamDisplay.CompleteAsync(); _cts = null; UpdateButtons(); }
        void PreservePartial()
        {
            var partial = streamDisplay.Snapshot();
            if (partial.Length > 0)
            {
                var marker = "\n[" + _l("Literary.Writer.Incomplete") + "]";
                _display.Add((false, partial + marker));
                streamDisplay.Report(new ModelStreamChunk(marker));
            }
        }
    }
}
