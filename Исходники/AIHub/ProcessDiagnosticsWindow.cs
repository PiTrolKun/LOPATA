using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub;

public sealed class ProcessDiagnosticsWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly QdrantRuntime _runtime = QdrantRuntime.Shared;
    private readonly ListBox _processes = new() { MinHeight = 130 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> _actions = [];
    private readonly Button _cancel = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _operation;

    public ProcessDiagnosticsWindow(Window owner, Func<string, string> localize)
    {
        Owner = owner; Resources = owner.Resources; ShowInTaskbar = false;
        Icon = owner.Icon; FontSize = owner.FontSize;
        _l = localize; Title = _l("Processes.Title"); Width = 780; Height = 570;
        MinWidth = 620; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = _l("Processes.Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        _processes.SetResourceReference(BackgroundProperty, "PanelBrush");
        _processes.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_processes);
        panel.Children.Add(new TextBlock { Text = "Qdrant " + QdrantOptions.Version, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 8) });
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock { Text = _l("Processes.QdrantHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var buttons = new WrapPanel();
        Add("Processes.Install", async token => await QdrantInstaller.InstallAsync(_runtime.Options,
            new Progress<double>(value => _result.Text = _l("Processes.Downloading") + $" {value:F0}%"), token));
        Add("Processes.Start", _runtime.StartAsync);
        Add("Processes.Stop", _ => _runtime.StopAsync());
        Add("Processes.Probe", async token =>
        {
            var result = await _runtime.ProbeAsync(token);
            _result.Text = _l(result.GracefulRestart ? "Processes.ProbePassed" : "Processes.ProbeForced") + $" RAM: {result.MemoryBytes / 1048576d:F1} MB";
        });
        _cancel.Content = _l("Processes.Cancel"); _cancel.Margin = new Thickness(0, 0, 6, 6); _cancel.IsEnabled = false;
        _cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        _cancel.Click += (_, _) => _operation?.Cancel(); buttons.Children.Add(_cancel);
        panel.Children.Add(buttons); panel.Children.Add(_result);
        var logs = new Button { Content = _l("Processes.Logs"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        logs.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        logs.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(OwnedProcessRegistry.LogDirectory); Process.Start(new ProcessStartInfo(OwnedProcessRegistry.LogDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { _result.Text = _l("Processes.Error") + " " + ex.Message; }
        };
        panel.Children.Add(logs);
        panel.Children.Add(new TextBlock { Text = _runtime.Options.DataDirectory, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _timer.Tick += (_, _) => Refresh(); Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _operation?.Cancel(); };

        void Add(string key, Func<CancellationToken, Task> action)
        {
            var button = new Button { Content = _l(key), Margin = new Thickness(0, 0, 6, 6) };
            button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            button.Click += async (_, _) => await RunAsync(action);
            _actions.Add(button); buttons.Children.Add(button);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        foreach (var button in _actions) button.IsEnabled = false;
        _cancel.IsEnabled = true; _result.Text = _l("Processes.Working");
        try { await action(cancellation.Token); if (_result.Text == _l("Processes.Working") || _result.Text.StartsWith(_l("Processes.Downloading"))) _result.Text = _l("Processes.Done"); }
        catch (OperationCanceledException) { _result.Text = _l("Processes.Cancelled"); }
        catch (Exception ex) { _result.Text = _l("Processes.Error") + " " + ex.Message; }
        finally
        {
            _operation = null; _cancel.IsEnabled = false;
            foreach (var button in _actions) button.IsEnabled = true;
            Refresh();
        }
    }
    private void Refresh()
    {
        _processes.ItemsSource = OwnedProcessRegistry.Shared.GetSnapshot().Select(p => $"{p.Component} · PID {p.Pid} · {p.MemoryBytes / 1048576d:F1} MB").ToArray();
        var state = _runtime.State == "Ready" && !_runtime.IsReady ? "Failed" : _runtime.State;
        _status.Text = _l("Processes.State." + state) + (_runtime.Pid is { } pid ? $" · PID {pid}" : "");
    }
}
