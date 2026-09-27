using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Lopata.Updates;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub;

public sealed class ApplicationUpdateWindow : Window
{
    private readonly ApplicationUpdateCoordinator _service;
    private readonly Func<string, string> _text;
    private readonly Func<Task> _install;
    private readonly Func<int> _connections;
    private readonly string _version;
    private readonly ApplicationUpdateSettings _settings;
    private readonly Action _save;
    private readonly Action _settingsChanged;
    private readonly Action _checkCompleted;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 12) };
    private readonly TextBox _notes = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 100, MaxHeight = 260, Padding = new(12)
    };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new(0, 12, 0, 12), Visibility = Visibility.Collapsed };
    private readonly Button _check = new(), _download = new(), _now = new(), _next = new(), _cancel = new(), _discard = new();
    private readonly RadioButton _stable = new(), _beta = new();
    private CancellationTokenSource? _operation;
    private UpdateOffer? _update;
    private PreparedApplicationUpdate? _ready;
    private UpdateDelivery? _direction;
    private bool _backgroundCheckBusy, _receivedBackgroundCheck;
    public bool IsBusy => _operation is not null;

    public void SetBackgroundCheckBusy(bool busy)
    {
        _backgroundCheckBusy = busy;
        if (busy) _status.Text = _text("Updates.Checking");
        RefreshButtons();
    }

    public void CompleteBackgroundCheck(UpdateOffer? offer, bool succeeded)
    {
        _backgroundCheckBusy = false;
        _receivedBackgroundCheck = true;
        if (_operation is null && _ready is null)
        {
            if (succeeded)
            {
                _update = offer;
                ShowRelease();
                if (offer is null) _status.Text = _text("Updates.CurrentLatest");
                else _ = ShowDownloadSizeAsync();
            }
            else if (_update is null) _status.Text = _text("Updates.CheckFailed");
        }
        RefreshButtons();
    }

    public ApplicationUpdateWindow(Window owner, ApplicationUpdateCoordinator service, ApplicationUpdateSettings settings,
        string version, Func<string, string> text, Action save, Func<int> connections, Func<Task> install, UpdateOffer? knownUpdate,
        Action? settingsChanged = null, Action? checkCompleted = null)
    {
        Owner = owner; Resources = owner.Resources;
        _service = service; _settings = settings; _version = version; _text = text;
        _save = save; _settingsChanged = settingsChanged ?? save; _checkCompleted = checkCompleted ?? (() => { });
        _connections = connections; _install = install;
        _direction = service.ReadDirection(); _ready = service.ReadPrepared();
        _update = _ready is null ? knownUpdate : ApplicationUpdateCoordinator.PreparedOffer(_ready);
        Title = text("Updates.Title"); Width = 740; Height = 710; MinWidth = 540; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var panel = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = text("Updates.Current") + " " + version, FontSize = 20 });
        var automatic = new CheckBox { Content = text("Updates.Automatic"), IsChecked = settings.CheckOnStartup, Margin = new(0, 16, 0, 16) };
        automatic.Click += (_, _) => { settings.CheckOnStartup = automatic.IsChecked == true; _settingsChanged(); };
        automatic.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(automatic);
        AddDirection(panel, _stable, UpdateDelivery.FullInstaller, "Updates.StableDirection");
        AddDirection(panel, _beta, UpdateDelivery.FilePatch, "Updates.BetaDirection");
        panel.Children.Add(new TextBlock { Text = text("Updates.DirectionHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        _notes.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _notes.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        panel.Children.Add(_status); panel.Children.Add(_notes); panel.Children.Add(_progress);
        var buttons = new WrapPanel { Margin = new(-4, 12, 0, 0) }; panel.Children.Add(buttons);
        AddButton(buttons, _check, "Updates.Check", CheckAsync);
        AddButton(buttons, _download, "Updates.Download", DownloadAsync);
        AddButton(buttons, _cancel, "Updates.Pause", () => { _operation?.Cancel(); return Task.CompletedTask; });
        AddButton(buttons, _now, "Updates.Now", async () =>
        {
            try { await _install(); }
            catch (Exception) { _status.Text = text("Updates.InstallFailed"); }
        });
        AddButton(buttons, _next, "Updates.NextLaunch", () =>
        {
            try { _service.Schedule(true); _ready = _service.ReadPrepared(); _status.Text = text("Updates.Scheduled"); RefreshButtons(); }
            catch (Exception) { _status.Text = text("Updates.InstallFailed"); }
            return Task.CompletedTask;
        });
        AddButton(buttons, _discard, "Updates.DiscardDecision", () =>
        {
            try { _service.CancelPrepared(); _ready = null; _update = null; ShowRelease(); RefreshButtons(); }
            catch (Exception) { _status.Text = text("Updates.InstallFailed"); }
            return Task.CompletedTask;
        });
        var close = new Button(); AddButton(buttons, close, "Updates.Later", () => { Close(); return Task.CompletedTask; });
        Closed += (_, _) => _operation?.Cancel();
        ShowRelease(); RefreshButtons();
        Loaded += async (_, _) =>
        {
            if (_backgroundCheckBusy || _receivedBackgroundCheck) return;
            if (_update is null && _direction is not null) await CheckAsync();
            else if (_update is not null && _ready is null) await ShowDownloadSizeAsync();
        };
    }

    public static bool IncludeBeta(ApplicationUpdateSettings settings, string version) =>
        settings.IncludeBeta ?? (ApplicationReleaseVersion.Parse(version)?.Channel is "beta" or "dev");

    private void AddDirection(Panel panel, RadioButton radio, UpdateDelivery delivery, string key)
    {
        radio.Content = new TextBlock { Text = _text(key), TextWrapping = TextWrapping.Wrap };
        radio.GroupName = "UpdateDirection"; radio.IsChecked = _direction == delivery; radio.Margin = new(0, 0, 0, 10);
        radio.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        radio.Click += (_, _) =>
        {
            try
            {
                _service.SaveDirection(delivery); _direction = delivery; _settings.LastCheckUtc = null; _settingsChanged();
                if (_ready is null) _update = null;
                ShowRelease(); RefreshButtons();
            }
            catch (Exception) { _status.Text = _text("Updates.SettingsFailed"); }
        };
        panel.Children.Add(radio);
    }

    private void AddButton(Panel panel, Button button, string key, Func<Task> action)
    {
        button.Content = _text(key); button.Margin = new(4); button.Padding = new(14, 10, 14, 10);
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }

    private async Task CheckAsync()
    {
        if (_backgroundCheckBusy || _operation is not null || _direction is null || _ready is not null) return;
        _operation = new(TimeSpan.FromSeconds(45)); RefreshButtons(); _status.Text = _text("Updates.Checking");
        try
        {
            _update = await _service.CheckAsync(_version, _direction.Value, _operation.Token);
            _settings.LastCheckUtc = _update is null ? DateTimeOffset.UtcNow : null; _save();
            ShowRelease();
            if (_update is not null) await ShowDownloadSizeAsync(_operation.Token);
            else _status.Text = _text("Updates.CurrentLatest");
        }
        catch (Exception) { _update = null; _status.Text = _text("Updates.CheckFailed"); }
        finally { _operation.Dispose(); _operation = null; _checkCompleted(); RefreshButtons(); }
    }

    private void ShowRelease()
    {
        _status.Text = _ready is not null ? _text(_ready.ApplyOnNextLaunch ? "Updates.Scheduled" : "Updates.Ready") + " " + _ready.Version
            : _direction is null ? _text("Updates.ChooseDirection")
            : _update is null ? _text("Updates.CheckHint") : _text("Updates.Available") + " " + _update.Version;
        _notes.Text = _update is null ? "" : string.Join(Environment.NewLine + Environment.NewLine,
            _update.Notes.Select(n => n.Version + (n.PublishedUtc == DateTimeOffset.MinValue ? "" : " · " + n.PublishedUtc.ToLocalTime().ToString("d"))
                + Environment.NewLine + n.Text));
        if (_update?.NotesIncomplete == true) _notes.Text = _text("Updates.NotesIncomplete") + Environment.NewLine + Environment.NewLine + _notes.Text;
        if (_update?.Delivery == UpdateDelivery.FilePatch && _service.Installation is null) _status.Text = _text("Updates.TransitionRequired");
    }

    private async Task ShowDownloadSizeAsync(CancellationToken token = default)
    {
        if (_update is null) return;
        var offer = _update;
        try
        {
            var size = await _service.DownloadSizeAsync(offer, token);
            if (ReferenceEquals(offer, _update) && _ready is null) { ShowRelease(); _status.Text += $" · {size / 1048576d:F1} MB"; }
        }
        catch (Exception) { if (ReferenceEquals(offer, _update)) _status.Text = _text("Updates.PlanFailed"); }
    }

    private async Task DownloadAsync()
    {
        if (_backgroundCheckBusy || _update is null || _operation is not null || _ready is not null) return;
        _operation = new(); RefreshButtons(); _progress.Visibility = Visibility.Visible;
        var progress = new Progress<ManagedModelDownloadProgress>(p =>
        {
            if (_operation is null) return;
            _progress.Value = p.TotalBytes == 0 ? 0 : Math.Clamp(p.DownloadedBytes * 100d / p.TotalBytes, 0, 100);
            _status.Text = p.Stage is "verifying" or "extracting" ? _text("Updates.Verifying")
                : $"{_text("Updates.Downloading")} {_progress.Value:F0}%";
        });
        try
        {
            await _service.PrepareAsync(_update, _connections(), progress, _operation.Token);
            _ready = _service.ReadPrepared(); _progress.Value = 100; ShowRelease();
        }
        catch (OperationCanceledException) { _status.Text = _text("Updates.Paused"); }
        catch (InvalidDataException) { _status.Text = _text("Updates.InvalidFile"); }
        catch (Exception) { _status.Text = _text("Updates.DownloadFailed"); }
        finally { _operation.Dispose(); _operation = null; _progress.Visibility = Visibility.Collapsed; RefreshButtons(); }
    }

    private void RefreshButtons()
    {
        var idle = _operation is null && !_backgroundCheckBusy;
        _stable.IsEnabled = _beta.IsEnabled = idle;
        _check.IsEnabled = idle && _direction is not null && _ready is null;
        _download.Visibility = _ready is null ? Visibility.Visible : Visibility.Collapsed;
        _download.IsEnabled = idle && _update is not null && (_update.Delivery == UpdateDelivery.FullInstaller || _service.Installation is not null);
        _cancel.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { _now, _next, _discard }) button.Visibility = _ready is null ? Visibility.Collapsed : Visibility.Visible;
        _now.IsEnabled = _discard.IsEnabled = idle;
        _next.IsEnabled = idle && _service.CanSchedule && _ready?.ApplyOnNextLaunch != true;
        _next.ToolTip = _service.CanSchedule ? null : _text("Updates.TransitionRequired");
        foreach (var button in new[] { _check, _download, _now, _next, _discard }) button.Opacity = button.IsEnabled ? 1 : 0.45;
    }
}
