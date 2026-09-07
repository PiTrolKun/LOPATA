using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub;

public sealed class ApplicationUpdateWindow : Window
{
    private readonly ApplicationUpdateService _service;
    private readonly Func<string, string> _text;
    private readonly Func<ApplicationUpdate, string, Task> _install;
    private readonly Func<int> _connections;
    private readonly string _version;
    private readonly ApplicationUpdateSettings _settings;
    private readonly Action _save;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 10) };
    private readonly TextBox _notes = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 70,
        MaxHeight = 220
    };
    private readonly ProgressBar _progress = new() { Height = 14, Minimum = 0, Maximum = 100, Margin = new(0, 12, 0, 12) };
    private readonly Button _check = new();
    private readonly Button _download = new();
    private readonly Button _installButton = new();
    private readonly Button _cancel = new();
    private readonly CheckBox _beta = new();
    private CancellationTokenSource? _operation;
    private ApplicationUpdate? _update;
    private string? _installer;

    public ApplicationUpdateWindow(Window owner, ApplicationUpdateService service, ApplicationUpdateSettings settings,
        string version, Func<string, string> text, Action save, Func<int> connections,
        Func<ApplicationUpdate, string, Task> install, ApplicationUpdate? knownUpdate)
    {
        Owner = owner;
        Resources = owner.Resources;
        _service = service; _settings = settings; _version = version; _text = text;
        _save = save; _connections = connections; _install = install; _update = knownUpdate;
        Title = text("Updates.Title"); Width = 620; Height = 560; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = text("Updates.Current") + " " + version, FontSize = 20 });
        var automatic = new CheckBox { Content = text("Updates.Automatic"), IsChecked = settings.CheckOnStartup, Margin = new(0, 16, 0, 8) };
        automatic.Click += (_, _) => { settings.CheckOnStartup = automatic.IsChecked == true; save(); };
        panel.Children.Add(automatic);
        automatic.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _beta.Content = text("Updates.Beta"); _beta.IsChecked = IncludeBeta(settings, version);
        _beta.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _notes.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _notes.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        _beta.Click += (_, _) =>
        {
            settings.IncludeBeta = _beta.IsChecked == true; settings.LastCheckUtc = null;
            _update = null; _installer = null; _notes.Text = ""; _status.Text = ""; save(); RefreshButtons();
        };
        panel.Children.Add(_beta);
        panel.Children.Add(_status); panel.Children.Add(_notes); panel.Children.Add(_progress);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        AddButton(buttons, _check, "Updates.Check", async () => await CheckAsync());
        AddButton(buttons, _download, "Updates.Download", async () => await DownloadAsync());
        AddButton(buttons, _cancel, "Updates.Pause", () => { _operation?.Cancel(); return Task.CompletedTask; });
        AddButton(buttons, _installButton, "Updates.Install", async () =>
        {
            if (_update is null || _installer is null) return;
            try { IsEnabled = false; await _install(_update, _installer); }
            catch (Exception) { _status.Text = text("Updates.InstallFailed"); }
            finally { IsEnabled = true; }
        });
        var close = new Button { Content = text("Updates.Later"), Margin = new(4) };
        close.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        close.Click += (_, _) => Close(); buttons.Children.Add(close);
        Closed += (_, _) => _operation?.Cancel();
        ShowRelease(); RefreshButtons();
        if (_update is null) Loaded += async (_, _) => await CheckAsync();
    }

    public static bool IncludeBeta(ApplicationUpdateSettings settings, string version) =>
        settings.IncludeBeta ?? (ApplicationReleaseVersion.Parse(version)?.Channel is "beta" or "dev");

    private void AddButton(Panel panel, Button button, string key, Func<Task> action)
    {
        button.Content = _text(key); button.Margin = new(4);
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }

    private async Task CheckAsync()
    {
        if (_operation is not null) return;
        _operation = new(TimeSpan.FromSeconds(25)); RefreshButtons();
        _status.Text = _text("Updates.Checking");
        try
        {
            _update = await _service.CheckAsync(_version, IncludeBeta(_settings, _version), _operation.Token);
            _installer = null; _settings.LastCheckUtc = _update is null ? DateTimeOffset.UtcNow : null; _save();
            ShowRelease();
        }
        catch (Exception) { _status.Text = _text("Updates.CheckFailed"); }
        finally { _operation.Dispose(); _operation = null; RefreshButtons(); }
    }

    private void ShowRelease()
    {
        _status.Text = _update is null ? _text("Updates.CurrentLatest")
            : $"{_text("Updates.Available")} {_update.Version} · {_update.Size / 1048576d:F1} MB";
        _notes.Text = _update?.Notes ?? "";
    }

    private async Task DownloadAsync()
    {
        if (_update is null || _operation is not null) return;
        _operation = new(); RefreshButtons();
        var progress = new Progress<ManagedModelDownloadProgress>(p =>
        {
            if (_operation is null) return;
            _progress.Value = p.TotalBytes == 0 ? 0 : p.DownloadedBytes * 100d / p.TotalBytes;
            _status.Text = p.Stage == "verifying" ? _text("Updates.Verifying")
                : $"{_text("Updates.Downloading")} {_progress.Value:F0}% · {p.BytesPerSecond / 1048576d:F1} MB/s";
        });
        try
        {
            _installer = await _service.DownloadAsync(_update, _connections(), progress, _operation.Token);
            _status.Text = _text("Updates.Ready"); _progress.Value = 100;
        }
        catch (OperationCanceledException) { _status.Text = _text("Updates.Paused"); }
        catch (InvalidDataException) { _status.Text = _text("Updates.InvalidFile"); }
        catch (Exception) { _status.Text = _text("Updates.DownloadFailed"); }
        finally { _operation.Dispose(); _operation = null; RefreshButtons(); }
    }

    private void RefreshButtons()
    {
        _check.IsEnabled = _beta.IsEnabled = _operation is null;
        _download.IsEnabled = _operation is null && _update is not null;
        _installButton.IsEnabled = _operation is null && _installer is not null;
        _cancel.IsEnabled = _operation is not null;
        foreach (var button in new[] { _check, _download, _installButton, _cancel })
            button.Opacity = button.IsEnabled ? 1 : 0.45;
    }
}
