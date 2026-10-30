using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Download and audit the pinned VACE files without starting a model runtime.</summary>
internal sealed partial class VideoVacePreparationControl : UserControl
{
    private Func<string, string> _localize;
    private readonly string _root;
    private readonly Func<IVideoVacePreparation> _create;
    private readonly IReadOnlyList<ManagedModelArtifactCard> _cards;
    private readonly VideoH3DownloadState _state;
    private readonly Dictionary<VideoH3FileRow, (TextBlock Status, TextBlock Size)> _rows = [];
    private CancellationTokenSource? _cancel;
    private TextBlock _status = new(), _speed = new(), _total = new();
    private System.Windows.Controls.ProgressBar _progress = new();
    private Button _verify = new(), _download = new(), _stop = new(), _next = new();
    private bool _initialCheckStarted, _exitPending, _exitHome, _navigationCompleted;
    private string _statusKey = "VacePageIdle";
    private int _run;

    internal bool Busy { get; private set; }
    internal bool Ready { get; private set; }
    internal event Action? BackRequested;
    internal event Action? HomeRequested;

    internal VideoVacePreparationControl(Func<string, string> localize, string root, int connections,
        Func<IVideoVacePreparation>? create = null)
    {
        _localize = localize; _root = root;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _create = create ?? (() => new VideoVacePreparation(root) { ParallelConnections = connections });
        using (var preparation = _create())
        { _cards = preparation.Cards; _state = new(_cards); _state.Refresh(preparation.Inspect()); }
        AutomationProperties.SetAutomationId(this, "VideoVace.Preparation");
        Build();
        Loaded += (_, _) =>
        {
            if (!Busy) _navigationCompleted = false;
            if (_initialCheckStarted || _workspaceActive) return;
            _initialCheckStarted = true; _ = RunAsync(false);
        };
        Unloaded += (_, _) => _cancel?.Cancel();
    }

    private string T(string key) => _localize("VideoPrep." + key);
    private TextBlock Text(string value, string? id = null, bool heading = false)
    {
        var text = MusicWishUi.Text(value, heading);
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.SetResourceReference(TextBlock.FontSizeProperty, heading ? "UiSectionFontSize" : "UiBodyFontSize");
        if (id is not null) AutomationProperties.SetAutomationId(text, id);
        return text;
    }
    private Button Action(string key, string id, Action action)
    {
        var button = MusicWishUi.Button(T(key), id, action);
        button.Padding = new Thickness(12, 8, 12, 8);
        if (button.Content is TextBlock label)
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
        return button;
    }
    private void Build()
    {
        if (BuildWorkspace()) return;
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(Text(T("VacePageTitle"), "VideoVace.Title", heading: true));
        header.Children.Add(Text(T("VacePageHint"), "VideoVace.Hint"));
        header.Children.Add(Text(string.Format(T("VacePageStorage"), _root), "VideoVace.Storage"));
        root.Children.Add(header);

        var files = new StackPanel(); _rows.Clear();
        foreach (var card in _cards)
        {
            files.Children.Add(Text(card.DisplayName + " · " + card.Revision[..Math.Min(8, card.Revision.Length)], heading: true));
            foreach (var row in _state.Rows.Where(r => r.CardId == card.ModelArtifactId))
            {
                var item = new Grid { Margin = new Thickness(0, 3, 0, 7) };
                item.ColumnDefinitions.Add(new()); item.ColumnDefinitions.Add(new() { Width = new GridLength(230) });
                var name = new StackPanel { Margin = new Thickness(10, 0, 14, 0) };
                name.Children.Add(Text(row.Path));
                var size = Text("", "VideoVace.Size." + row.CardId + "." + row.Path); name.Children.Add(size); item.Children.Add(name);
                var status = Text("", "VideoVace.File." + row.CardId + "." + row.Path);
                Grid.SetColumn(status, 1); item.Children.Add(status);
                _rows.Add(row, (status, size)); files.Children.Add(item);
            }
        }
        var scroll = new ScrollViewer { Content = files, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "VideoVace.Files"); Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _status = Text("", "VideoVace.Status"); _speed = Text("", "VideoVace.Speed"); _total = Text("", "VideoVace.Total");
        footer.Children.Add(_status); footer.Children.Add(_speed); footer.Children.Add(_total);
        _progress = new() { Height = 7, Maximum = 100, Margin = new Thickness(0, 5, 0, 10) };
        AutomationProperties.SetAutomationId(_progress, "VideoVace.Progress"); footer.Children.Add(_progress);
        var actions = new WrapPanel();
        actions.Children.Add(Action("VacePageBack", "VideoVace.Back", RequestBack));
        actions.Children.Add(Action("VacePageHome", "VideoVace.Home", RequestHome));
        _verify = Action("VacePageVerify", "VideoVace.Verify", () => _ = RunAsync(false)); actions.Children.Add(_verify);
        _download = Action("VacePageDownload", "VideoVace.Download", () => _ = RunAsync(true)); actions.Children.Add(_download);
        _stop = Action("VacePageStop", "VideoVace.Cancel", () => _cancel?.Cancel()); actions.Children.Add(_stop);
        _next = Action("VacePageNext", "VideoVace.Next", OpenWorkspace); actions.Children.Add(_next);
        footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root; Refresh();
    }

    internal void Configure(Func<string, string> localize) { _localize = localize; Build(); }
    internal bool CanCheckpoint() => _workspace?.TryCommit() ?? true;
    internal void RequestBack()
    {
        if (_workspaceActive) { if (!CanCheckpoint()) return; _workspaceActive = false; Build(); return; }
        RequestExit();
    }
    internal bool RequestExit()
    {
        if (!CanCheckpoint()) return false;
        RequestNavigation(home: false);
        return true;
    }
    private void RequestHome() => RequestNavigation(home: true);
    private void RequestNavigation(bool home)
    {
        if (_exitPending || _navigationCompleted) return;
        // Cancellation can finish synchronously. Record the exit before cancelling and emit it once.
        _exitPending = true; _exitHome = home;
        if (Busy) _cancel?.Cancel(); else CompleteNavigation();
    }
    private void CompleteNavigation()
    {
        if (_navigationCompleted) return;
        _exitPending = false; _navigationCompleted = true;
        if (_exitHome) HomeRequested?.Invoke(); else BackRequested?.Invoke();
    }
    private void Refresh()
    {
        foreach (var (row, controls) in _rows)
        {
            controls.Status.Text = (row.Stage == "ready" ? "✓ " : row.Stage == "downloading" ? "↓ " : "○ ") + T("VaceFile." + row.Stage);
            controls.Size.Text = ComponentCardViewModel.FormatBytes(row.Stored) + " / " + ComponentCardViewModel.FormatBytes(row.Size);
        }
        _speed.Text = string.Format(T("VacePageSpeed"), _state.BytesPerSecond > 0
            ? ComponentCardViewModel.FormatBytes((long)_state.BytesPerSecond) + T("VacePerSecond") : "—");
        _total.Text = string.Format(T("VacePageTotal"), ComponentCardViewModel.FormatBytes(_state.StoredBytes), ComponentCardViewModel.FormatBytes(_state.TotalBytes));
        _status.Text = T(_statusKey);
        _progress.Value = _state.TotalBytes == 0 ? 0 : 100d * _state.StoredBytes / _state.TotalBytes;
        _progress.IsIndeterminate = Busy && !_state.Rows.Any(r => r.Stage is "downloading" or "assembling");
        _verify.IsEnabled = !Busy; _download.IsEnabled = !Busy && !Ready; _stop.IsEnabled = Busy;
        _next.Visibility = Ready && !Busy && CreateWorkspace is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal async Task RunAsync(bool download)
    {
        if (Busy || _exitPending || _navigationCompleted) return;
        Busy = true; Ready = false; _statusKey = download ? "VacePageDownloading" : "VacePageChecking";
        var run = ++_run;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _cancel = cancel; Refresh();
        IVideoVacePreparation? preparation = null;
        try
        {
            preparation = _create();
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            var ready = await preparation.PrepareAsync(download, new Progress<ManagedModelDownloadProgress>(p =>
            {
                if (run != _run || !Busy) return;
                _state.Apply(p);
                _statusKey = p.Stage switch { "downloading" => "VacePageDownloading", "restarting" => "VacePageRestarting", _ => "VacePageChecking" };
                Refresh();
            }), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            Ready = ready; _state.Refresh(preparation.Inspect(), ready);
            _statusKey = ready ? "VaceFilesReady" : "VacePageMissing";
        }
        catch (OperationCanceledException)
        { _statusKey = "VacePageCancelled"; if (preparation is not null) _state.Refresh(preparation.Inspect()); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error); _statusKey = "VacePageFailed";
            if (preparation is not null) _state.Fail(preparation.Inspect());
        }
        finally
        {
            preparation?.Dispose(); Busy = false;
            if (ReferenceEquals(_cancel, cancel)) _cancel = null;
            cancel.Dispose(); Refresh();
            if (_exitPending) CompleteNavigation();
        }
    }
}
