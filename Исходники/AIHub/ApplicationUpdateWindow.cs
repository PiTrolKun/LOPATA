using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Controls;
using Lopata.Updates;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

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
    private readonly TextBlock _checkStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly TextBox _notes = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 100, MaxHeight = 260, Padding = new(12)
    };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new(0, 12, 0, 12), Visibility = Visibility.Collapsed };
    private readonly Button _retry = new(), _download = new(), _cancel = new();
    private readonly RadioButton _stable, _beta;
    private CancellationTokenSource? _operation;
    private UpdateOffer? _update;
    private PreparedApplicationUpdate? _ready;
    private UpdateDelivery? _direction;
    private bool _backgroundCheckBusy, _receivedBackgroundCheck;
    private bool _checkFailed, _downloading, _choosing, _installing, _closed;
    public bool IsBusy => _operation is not null || _choosing || _installing;
    public UpdateOffer? AvailableUpdate => _update;

    public void SetBackgroundCheckBusy(bool busy)
    {
        _backgroundCheckBusy = busy;
        if (busy) _checkStatus.Text = _text("Updates.Checking");
        RefreshButtons();
    }

    public void CompleteBackgroundCheck(UpdateOffer? offer, bool succeeded)
    {
        _backgroundCheckBusy = false;
        _receivedBackgroundCheck = true;
        if (_closed) return;
        if (_operation is null)
        {
            _checkFailed = !succeeded;
            _checkStatus.Text = _text(succeeded ? offer is null ? "Updates.CurrentLatest" : "Updates.Available" : "Updates.CheckFailed")
                + (succeeded && offer is not null ? " " + offer.Version : "");
            if (succeeded)
            {
                if (_ready is null) _update = offer;
                ShowRelease();
                if (_ready is null && offer is not null) _ = ShowDownloadSizeAsync();
            }
            else if (_ready is null) { _update = null; ShowRelease(); }
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
        var root = new Grid { Margin = new(24) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(new TextBlock { Text = text("Updates.Current") + " " + version, FontSize = 20 });
        var automatic = new CheckBox { Content = text("Updates.Automatic"), IsChecked = settings.CheckOnStartup, Margin = new(0, 16, 0, 16) };
        automatic.Click += (_, _) => { settings.CheckOnStartup = automatic.IsChecked == true; _settingsChanged(); };
        automatic.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush"); panel.Children.Add(automatic);
        _stable = ApplicationUpdateChoices.Direction(text, beta: false);
        _beta = ApplicationUpdateChoices.Direction(text, beta: true);
        AddDirection(panel, _stable, UpdateDelivery.FullInstaller);
        AddDirection(panel, _beta, UpdateDelivery.FilePatch);
        panel.Children.Add(new TextBlock { Text = text("Updates.DirectionHelp"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        _notes.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _notes.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        panel.Children.Add(_checkStatus); panel.Children.Add(_status); panel.Children.Add(_notes); panel.Children.Add(_progress);
        var buttons = new WrapPanel { Margin = new(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        AddButton(buttons, _retry, "Updates.Retry", CheckAsync);
        AddButton(buttons, _download, "Updates.DownloadAction", ChooseTimingAsync, primary: true);
        AddButton(buttons, _cancel, "Updates.Pause", () => { _operation?.Cancel(); return Task.CompletedTask; });
        Closed += (_, _) => { _closed = true; _operation?.Cancel(); };
        ShowRelease(); RefreshButtons();
        Loaded += async (_, _) =>
        {
            if (_backgroundCheckBusy || _receivedBackgroundCheck) return;
            await CheckAsync();
        };
    }

    public static bool IncludeBeta(ApplicationUpdateSettings settings, string version) =>
        settings.IncludeBeta ?? (ApplicationReleaseVersion.Parse(version)?.Channel is "beta" or "dev");

    private void AddDirection(Panel panel, RadioButton radio, UpdateDelivery delivery)
    {
        radio.IsChecked = _direction == delivery;
        radio.Click += async (_, _) =>
        {
            if (_direction == delivery) return;
            try
            {
                _service.SaveDirection(delivery); _direction = delivery; _settings.LastCheckUtc = null; _settingsChanged();
                if (_ready is null) _update = null;
                ShowRelease(); RefreshButtons();
                await CheckAsync();
            }
            catch (Exception)
            {
                _stable.IsChecked = _direction == UpdateDelivery.FullInstaller;
                _beta.IsChecked = _direction == UpdateDelivery.FilePatch;
                _status.Text = _text("Updates.SettingsFailed"); _status.Visibility = Visibility.Visible;
            }
        };
        panel.Children.Add(radio);
    }

    private void AddButton(Panel panel, Button button, string key, Func<Task> action, bool primary = false)
    {
        ApplicationUpdateChoices.Button(button, _text(key), primary);
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }

    private async Task CheckAsync()
    {
        if (_closed || _backgroundCheckBusy || IsBusy || _direction is null) return;
        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _operation = operation; _checkFailed = false; RefreshButtons(); _checkStatus.Text = _text("Updates.Checking");
        try
        {
            var offer = await _service.CheckAsync(_version, _direction.Value, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            if (_ready is null) _update = offer;
            _settings.LastCheckUtc = offer is null ? DateTimeOffset.UtcNow : null; _save();
            _checkStatus.Text = _text(offer is null ? "Updates.CurrentLatest" : "Updates.Available") + (offer is null ? "" : " " + offer.Version);
            ShowRelease();
            if (_ready is null && _update is not null) await ShowDownloadSizeAsync(operation.Token);
        }
        catch (Exception)
        {
            if (!_closed)
            {
                _checkFailed = true; _checkStatus.Text = _text("Updates.CheckFailed");
                if (_ready is null) { _update = null; ShowRelease(); }
            }
        }
        finally { operation.Dispose(); _operation = null; _checkCompleted(); RefreshButtons(); }
    }

    private void ShowRelease()
    {
        _status.Text = _ready is not null ? _text(_ready.ApplyOnNextLaunch ? "Updates.Scheduled" : "Updates.Ready") + " " + _ready.Version
            : _direction is null ? _text("Updates.ChooseDirection")
            : "";
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        _notes.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        _notes.Text = _update is null ? "" : string.Join(Environment.NewLine + Environment.NewLine,
            _update.Notes.Select(n => n.Version + (n.PublishedUtc == DateTimeOffset.MinValue ? "" : " · " + n.PublishedUtc.ToLocalTime().ToString("d"))
                + Environment.NewLine + n.Text));
        if (_update?.NotesIncomplete == true) _notes.Text = _text("Updates.NotesIncomplete") + Environment.NewLine + Environment.NewLine + _notes.Text;
        if (_update?.Delivery == UpdateDelivery.FilePatch && _service.Installation is null)
        {
            _status.Text = _text("Updates.TransitionRequired"); _status.Visibility = Visibility.Visible;
        }
    }

    private async Task ShowDownloadSizeAsync(CancellationToken token = default)
    {
        if (_update is null) return;
        var offer = _update;
        try
        {
            var size = await _service.DownloadSizeAsync(offer, token);
            if (!_closed && ReferenceEquals(offer, _update) && _ready is null)
                _checkStatus.Text = _text("Updates.Available") + " " + offer.Version + $" · {size / 1048576d:F1} MB";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!_closed && ReferenceEquals(offer, _update)) { _status.Text = _text("Updates.PlanFailed"); _status.Visibility = Visibility.Visible; } }
    }

    private async Task ChooseTimingAsync()
    {
        if (_closed || _backgroundCheckBusy || IsBusy || _update is null) return;
        _choosing = true; RefreshButtons();
        bool? nextLaunch;
        try
        {
            var question = new ApplicationUpdateTimingWindow(this, _text, _service.CanSchedule, _ready is not null);
            nextLaunch = question.ShowDialog() == true ? question.ApplyOnNextLaunch : null;
        }
        finally { _choosing = false; RefreshButtons(); }
        if (nextLaunch is null || _closed) return;
        if (_ready is not null) await ApplyTimingAsync(nextLaunch.Value);
        else await DownloadAsync(nextLaunch.Value);
    }

    private async Task ApplyTimingAsync(bool nextLaunch)
    {
        if (_closed || _ready is null) return;
        _installing = true; RefreshButtons();
        try
        {
            _service.Schedule(nextLaunch); _ready = _service.ReadPrepared(); ShowRelease();
            if (!nextLaunch && !_closed) await _install();
        }
        catch (Exception) { _status.Text = _text("Updates.InstallFailed"); _status.Visibility = Visibility.Visible; }
        finally { _installing = false; RefreshButtons(); }
    }

    private async Task DownloadAsync(bool nextLaunch)
    {
        if (_closed || _backgroundCheckBusy || IsBusy || _update is null || _ready is not null) return;
        var operation = new CancellationTokenSource();
        _operation = operation; _downloading = true; RefreshButtons();
        _progress.Value = 0; _progress.Visibility = Visibility.Visible; _status.Visibility = Visibility.Visible;
        _status.Text = _text("Updates.Downloading");
        var verified = false;
        var progress = new Progress<ManagedModelDownloadProgress>(p =>
        {
            if (_closed || operation.IsCancellationRequested || !ReferenceEquals(_operation, operation) || !_downloading) return;
            _progress.Value = p.TotalBytes == 0 ? 0 : Math.Clamp(p.DownloadedBytes * 100d / p.TotalBytes, 0, 100);
            _status.Text = p.Stage is "verifying" or "extracting" ? _text("Updates.Verifying")
                : p.Stage == "retrying" ? _text("Updates.RetryingDownload")
                : $"{_text("Updates.Downloading")} {_progress.Value:F0}%";
        });
        try
        {
            await _service.PrepareAsync(_update, _connections(), progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            _ready = _service.ReadPrepared(); _progress.Value = 100; ShowRelease();
            verified = _ready is not null;
        }
        catch (OperationCanceledException) { _status.Text = _text("Updates.Paused"); }
        catch (InvalidDataException error)
        {
            ApplicationUpdateDiagnostics.RecordFailure(_update?.Version, error);
            _status.Text = _text("Updates.InvalidFile");
        }
        catch (Exception error)
        {
            ApplicationUpdateDiagnostics.RecordFailure(_update?.Version, error);
            _status.Text = _text("Updates.DownloadFailed");
        }
        finally { operation.Dispose(); _operation = null; _downloading = false; _progress.Visibility = Visibility.Collapsed; RefreshButtons(); }
        if (verified && !_closed) await ApplyTimingAsync(nextLaunch);
    }

    private void RefreshButtons()
    {
        var idle = !IsBusy && !_backgroundCheckBusy && !_closed;
        _stable.IsEnabled = _beta.IsEnabled = idle;
        _retry.Visibility = _checkFailed ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = idle && _direction is not null;
        ApplicationUpdateChoices.Button(_download, _text(_ready is null ? "Updates.DownloadAction"
            : _ready.ApplyOnNextLaunch ? "Updates.ChangeTiming" : "Updates.Install"), primary: true);
        _download.Visibility = _update is null || _downloading ? Visibility.Collapsed : Visibility.Visible;
        _download.IsEnabled = idle && _update is not null && (_ready is not null || _update.Delivery == UpdateDelivery.FullInstaller || _service.Installation is not null);
        _cancel.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
        _cancel.IsEnabled = !_closed && _operation?.IsCancellationRequested == false;
        foreach (var button in new[] { _retry, _download, _cancel }) button.Opacity = button.IsEnabled ? 1 : 0.45;
    }
}
