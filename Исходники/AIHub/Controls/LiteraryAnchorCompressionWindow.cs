using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>A copyable answer only: no chat, input, persistent state or automatic replacement.</summary>
public sealed class LiteraryAnchorCompressionWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly Func<IProgress<ModelStreamChunk>, CancellationToken, Task<string>> _generate;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBox _answer = LiteraryWorkspaceParts.TextArea();
    private readonly TextBlock _status = LiteraryUi.Text("");
    private readonly Button _stop;
    private bool _started, _closing, _closed;
    public bool IsWorking { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action? WorkingChanged;

    public LiteraryAnchorCompressionWindow(Func<string, string> localize,
        Func<IProgress<ModelStreamChunk>, CancellationToken, Task<string>> generate)
    {
        _l = localize; _generate = generate;
        Title = _l("Literary.Anchor.CompressTitle");
        Width = 780; Height = 620; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main && main != this)
            Resources.MergedDictionaries.Add(main.Resources);
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var hint = LiteraryUi.Text(_l("Literary.Anchor.CompressHint"));
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_status);
        _stop = LiteraryUi.Button(_l("Paragraph.Stop"), () => _cancel.Cancel());
        _stop.Margin = new Thickness(0, 8, 0, 0); bottom.Children.Add(_stop);
        root.Children.Add(_answer);
        Loaded += (_, _) => { if (!_started) { _started = true; Completion = GenerateAsync(); } };
        Closing += OnClosing;
        Closed += (_, _) => { _closed = true; _cancel.Dispose(); };
    }

    private async Task GenerateAsync()
    {
        IsWorking = true; _stop.IsEnabled = true; WorkingChanged?.Invoke();
        _status.Text = _l("Paragraph.Working");
        var raw = new StringBuilder();
        try
        {
            var result = await _generate(new InlineProgress<ModelStreamChunk>(chunk => Dispatcher.Invoke(() =>
            { raw.Append(chunk.Text); _answer.Text = raw.ToString(); _answer.ScrollToEnd(); })), _cancel.Token);
            _answer.Text = result;
            _status.Text = _l("Literary.Anchor.CompressDone");
        }
        catch (OperationCanceledException) { _status.Text = _l("Paragraph.Cancelled"); }
        catch (ImageAnalysisContextExhaustedException ex) { _status.Text = LiteraryContextBudgetMessage.Format(ex, _l); }
        catch (Exception) { _status.Text = _l("Paragraph.Failure"); }
        finally { IsWorking = false; _stop.IsEnabled = false; WorkingChanged?.Invoke(); }
    }

    public async Task CloseWhenStoppedAsync()
    {
        if (_closed) return;
        _cancel.Cancel();
        await Completion;
        if (_closed) return;
        _closing = true; Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing || !IsWorking) return;
        e.Cancel = true;
        await CloseWhenStoppedAsync();
    }
}
