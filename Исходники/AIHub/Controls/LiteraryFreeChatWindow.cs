using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>All conversation state is owned by this window and intentionally has no store or diagnostics.</summary>
public sealed class LiteraryFreeChatWindow : Window
{
    private readonly LiteraryChatRuntime _runtime;
    private readonly Func<string,string> _l;
    private readonly List<ImageAnalysisHiddenMessage> _conversation = [];
    private readonly TextBox _history = LiteraryWorkspaceParts.TextArea(), _input = LiteraryWorkspaceParts.TextArea(false);
    private readonly Button _send = new(), _stop = new(), _clear = new();
    private readonly TextBlock _status = LiteraryUi.Text("");
    private CancellationTokenSource? _operation;
    private bool _closing;
    public bool IsWorking => _operation is not null;
    public LiteraryFreeChatWindow(LiteraryChatRuntime runtime, Func<string,string> l, string language)
    {
        _runtime = runtime; _l = l; Title = l("Studio.FreeChat"); Width = 780; Height = 690; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main) Resources = main.Resources;
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var hint = LiteraryUi.Text(l("Studio.FreeHint")); DockPanel.SetDock(hint,Dock.Top); root.Children.Add(hint);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        _input.MinHeight = 0; _input.MinLines = 2; _input.MaxLines = 6; LiterarySpellChecking.Enable(_input,language); bottom.Children.Add(_input);
        var actions = new WrapPanel();
        foreach (var (button,label) in new[] { (_send,"Literary.Writer.Send"),(_stop,"Paragraph.Stop"),(_clear,"Paragraph.Clear") })
        { button.Content = l(label); button.SetResourceReference(StyleProperty,"SecondaryButtonStyle"); button.Margin = new Thickness(0,8,8,4); actions.Children.Add(button); }
        bottom.Children.Add(actions); bottom.Children.Add(_status); root.Children.Add(_history);
        _send.Click += async (_,_) => await SendAsync(); _stop.Click += (_,_) => _operation?.Cancel();
        _clear.Click += (_,_) => { _conversation.Clear(); _history.Clear(); _status.Text = ""; };
        _input.TextChanged += (_,_) => Availability();
        _input.ToolTip = l("Studio.InputHint");
        _input.PreviewKeyDown += async (_,e) =>
        {
            if (e.Key==System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers is
                System.Windows.Input.ModifierKeys.None or System.Windows.Input.ModifierKeys.Control)
            { e.Handled=true; if (!e.IsRepeat && _send.IsEnabled) await SendAsync(); }
        };
        Closing += OnClosing; Closed += (_,_) => { _conversation.Clear(); _history.Clear(); _input.Clear(); }; Availability();
    }
    private void Availability()
    { _send.IsEnabled = !IsWorking && !string.IsNullOrWhiteSpace(_input.Text); _stop.IsEnabled = IsWorking; _clear.IsEnabled = !IsWorking; _input.IsReadOnly = IsWorking; }
    private async Task SendAsync()
    {
        if (IsWorking || string.IsNullOrWhiteSpace(_input.Text)) return;
        var text = _input.Text; var raw = new StringBuilder(); var previous = _history.Text;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _status.Text = _l(_runtime.IsBusy ? "Studio.Queued" : "Paragraph.Working"); Availability();
        try
        {
            var request = _conversation.Concat([new ImageAnalysisHiddenMessage { Role="user",Content=text }]).ToArray();
            var prefix = previous + "\n\n" + _l("Studio.Role.User") + ":\n" + text + "\n\n" + _l("Studio.Model") + ":\n";
            var result = await _runtime.FreeChatAsync(request,new InlineProgress<ModelStreamChunk>(chunk=>Dispatcher.Invoke(()=>
            { raw.Append(chunk.Text); _history.Text = prefix + raw; _history.ScrollToEnd(); })),cancellation.Token,
                count=>Dispatcher.Invoke(()=>_status.Text = _l("Paragraph.Tokens") + " " + count + " / " + _runtime.ContextCapacity));
            _conversation.Add(request[^1]); _conversation.Add(new() { Role="assistant",Content=result });
            _history.Text = prefix + result; _input.Clear(); _status.Text = "";
        }
        catch (OperationCanceledException) { _status.Text = _l("Paragraph.Cancelled"); }
        catch (ImageAnalysisContextExhaustedException ex) { _status.Text = LiteraryContextBudgetMessage.Format(ex, _l); }
        catch (Exception) { _status.Text = _l("Paragraph.Failure"); }
        finally { _operation = null; Availability(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || _operation is null) return;
        e.Cancel = true; _operation.Cancel();
        while (_operation is not null) await Task.Delay(50);
        _closing = true; Close();
    }
}
