using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed class LiteraryPreparationControl : UserControl
{
    private readonly Func<string, string> _l;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress = new() { Height = 10, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 10, 0, 14) };
    private readonly Button _install, _next, _retry;
    private CancellationTokenSource? _cancel;
    private bool _busy;
    private long _lastUiUpdate;
    public event Action? Ready;
    public event Action? BackRequested;
    public event Action? HomeRequested;
    public void Cancel() => _cancel?.Cancel();

    public LiteraryPreparationControl(Func<string, string> l)
    {
        _l = l;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        _progress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
        _progress.SetResourceReference(ProgressBar.BackgroundProperty, "PanelBrush");
        var panel = new StackPanel { MaxWidth = 1000, Margin = new Thickness(40), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        panel.Children.Add(LiteraryUi.Text(l("Literary.Prepare.Title"), true));
        panel.Children.Add(LiteraryUi.Text(l("Literary.Prepare.Hint")));
        panel.Children.Add(_rows);
        _status = LiteraryUi.Text(""); panel.Children.Add(_status); panel.Children.Add(_progress);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _install = LiteraryUi.Button(l("Literary.Prepare.Install"), () => _ = RunAsync(true), true);
        _retry = LiteraryUi.Button(l("Literary.Prepare.Check"), () => _ = RunAsync(false));
        _next = LiteraryUi.Button(l("Literary.Prepare.Next"), () => _ = ContinueAsync(), true); _next.IsEnabled = false;
        _next.Opacity = 0.45;
        _next.IsEnabledChanged += (_, _) => _next.Opacity = _next.IsEnabled ? 1 : 0.45;
        actions.Children.Add(_install); actions.Children.Add(_retry); actions.Children.Add(_next); panel.Children.Add(actions);
        var navigation = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };
        navigation.Children.Add(LiteraryUi.Button(l("Literary.Prepare.Stop"), Cancel));
        navigation.Children.Add(LiteraryUi.Button(l("Literary.Back"), () => { Cancel(); BackRequested?.Invoke(); }));
        navigation.Children.Add(LiteraryUi.Button(l("Literary.Home"), () => { Cancel(); HomeRequested?.Invoke(); })); panel.Children.Add(navigation);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { if (_rows.Children.Count == 0 && !_busy) _ = RunAsync(false); };
        Unloaded += (_, _) => Cancel();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Cancel(); };
    }
    private void Report(LiteraryPreparationProgress value)
    {
        var now = Environment.TickCount64;
        if (value.Percent is >= 0 and < 100 && now - _lastUiUpdate < 120) return;
        _lastUiUpdate = now;
        _status.Text = _l("Literary.Prepare." + value.Stage) + (value.Detail.Length > 0 ? " · " + value.Detail : "")
            + (value.Percent >= 0 ? $" · {value.Percent:0}%" : "");
        _progress.IsIndeterminate = value.Percent < 0; if (value.Percent >= 0) _progress.Value = value.Percent;
    }
    private async Task RunAsync(bool install)
    {
        if (_busy) return;
        _busy = true; _cancel = new(); _install.IsEnabled = _retry.IsEnabled = _next.IsEnabled = false;
        var ct = _cancel.Token;
        try
        {
            var progress = new Progress<LiteraryPreparationProgress>(Report);
            if (install) await LiteraryPreparation.InstallAsync(progress, ct);
            var states = await LiteraryPreparation.CheckAsync(progress, ct);
            ct.ThrowIfCancellationRequested();
            _rows.Children.Clear();
            foreach (var state in states) _rows.Children.Add(LiteraryUi.Text(
                _l("Literary.Prepare." + state.Key) + " — " + _l(state.Ready ? "Literary.Prepare.Available" : "Literary.Prepare.Missing")));
            var ready = states.All(s => s.Ready);
            _next.IsEnabled = ready;
            _status.Text = _l(ready ? "Literary.Prepare.AllReady" : "Literary.Prepare.NeedsDownload");
        }
        catch (OperationCanceledException) { _status.Text = _l("Literary.Prepare.Cancelled"); }
        catch (Exception ex) { _status.Text = _l("Literary.Prepare.Error") + " " + ex.Message; }
        finally { _progress.IsIndeterminate = false; _busy = false; _install.IsEnabled = _retry.IsEnabled = true; _cancel.Dispose(); _cancel = null; }
    }
    private async Task ContinueAsync()
    {
        if (_busy) return;
        _busy = true; _next.IsEnabled = _install.IsEnabled = _retry.IsEnabled = false; _cancel = new();
        try
        {
            await ComponentLicenseGate.EnsureAsync(LiteraryPreparation.Licenses, _cancel.Token);
            await QdrantRuntime.Shared.StartAsync(_cancel.Token);
            await LiterarySourceIndex.RecoverAsync(_cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested(); Ready?.Invoke();
        }
        catch (OperationCanceledException) { _status.Text = _l("Literary.Prepare.Cancelled"); }
        catch (Exception ex) { _status.Text = _l("Literary.Prepare.Error") + " " + ex.Message; }
        finally { _busy = false; _next.IsEnabled = _install.IsEnabled = _retry.IsEnabled = true; _cancel.Dispose(); _cancel = null; }
    }
}
