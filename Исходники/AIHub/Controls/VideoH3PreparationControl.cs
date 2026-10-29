using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>The first H3 model page, hosted inside the scenario rather than in a modal window.</summary>
internal sealed class VideoH3PreparationControl : UserControl
{
    private Func<string, string> _l;
    private readonly string _root;
    private readonly Func<IVideoH3Preparation> _create;
    private readonly IReadOnlyList<ManagedModelArtifactCard> _cards;
    private readonly VideoH3DownloadState _state;
    private readonly Dictionary<VideoH3FileRow, (TextBlock Status, TextBlock Size)> _rows = [];
    private CancellationTokenSource? _cancel;
    private TextBlock _status = new(), _speed = new(), _total = new();
    private System.Windows.Controls.ProgressBar _progress = new();
    private Button _verify = new(), _download = new(), _stop = new(), _next = new();
    private bool _returnAfterCancel, _ready, _workspaceActive;
    private string _statusKey = "H3PageIdle";
    private int _run;
    private VideoH3WorkspaceControl? _workspace;
    internal Func<VideoH3WorkspaceControl>? CreateWorkspace { get; set; }
    internal bool CanCheckpoint() => _workspace?.TryCommit() ?? true;
    internal void SetWorkspaceBusy(bool busy) => _workspace?.SetBusy(busy);
    internal void NotifyWorkspace(string message) => _workspace?.Notify(message);
    internal void RefreshResults() => _workspace?.ReloadResults();
    internal void SetGenerating(bool busy, bool pending = false) => _workspace?.SetGenerating(busy, pending);
    internal void SetScheduled(DateTimeOffset? due) => _workspace?.SetScheduled(due);
    internal void GenerationProgress(string message) => _workspace?.GenerationProgress(message);
    internal void ShowSavedResults() { _workspaceActive = true; Build(); _workspace?.ReloadResults(); }
    internal bool Busy { get; private set; }
    internal bool Ready => _ready;
    internal event Action? BackRequested;
    internal event Action? HomeRequested;
    internal VideoH3PreparationControl(Func<string, string> localize, string root, int connections,
        Func<IVideoH3Preparation>? create = null)
    {
        _l = localize; _root = root;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _create = create ?? (() => new VideoH3Preparation(root) { ParallelConnections = connections });
        using (var source = _create()) { _cards = source.Cards; _state = new(_cards); _state.Refresh(source.Inspect()); }
        AutomationProperties.SetAutomationId(this, "VideoH3.Preparation");
        Build(); Loaded += (_, _) => { if (!Busy && !_workspaceActive) _ = RunAsync(false); };
        Unloaded += (_, _) => _cancel?.Cancel();
    }
    private string T(string key) => _l("VideoPrep." + key);
    private TextBlock Text(string text, string? id = null, bool heading = false)
    {
        var block = MusicWishUi.Text(text, heading); block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        block.SetResourceReference(TextBlock.FontSizeProperty, heading ? "UiSectionFontSize" : "UiBodyFontSize");
        if (id is not null) AutomationProperties.SetAutomationId(block, id); return block;
    }
    private Button Button(string key, string id, Action action)
    {
        var button = MusicWishUi.Button(T(key), id, action); button.Padding = new Thickness(12, 8, 12, 8); return button;
    }
    private void Build()
    {
        if (_workspaceActive)
        {
            if (_workspace is null)
            {
                _workspace = CreateWorkspace?.Invoke() ?? new VideoH3WorkspaceControl(_l);
                _workspace.BackRequested += RequestBack;
                _workspace.HomeRequested += () => HomeRequested?.Invoke();
            }
            _workspace.Configure(_l); Content = _workspace; return;
        }
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Text(_l("Navigation.Creation") + "  ›  " + _l("Navigation.Video") + "  ›  MiniMax H3", heading: true));
        heading.Children.Add(Text(T("H3PageTitle"), heading: true)); heading.Children.Add(Text(T("H3PageHint")));
        heading.Children.Add(Text(string.Format(T("H3PageStorage"), _root), "VideoH3.Storage")); root.Children.Add(heading);
        var list = new StackPanel(); _rows.Clear();
        foreach (var card in _cards)
        {
            list.Children.Add(Text(card.DisplayName + " · " + card.Revision[..Math.Min(8, card.Revision.Length)], heading: true));
            foreach (var row in _state.Rows.Where(r => r.CardId == card.ModelArtifactId))
            {
                var item = new Grid { Margin = new Thickness(0, 3, 0, 7) };
                item.ColumnDefinitions.Add(new()); item.ColumnDefinitions.Add(new() { Width = new GridLength(260) });
                var name = new StackPanel { Margin = new Thickness(10, 0, 14, 0) }; name.Children.Add(Text(row.Path));
                var size = Text("", "VideoH3.Size." + row.CardId + "." + row.Path); name.Children.Add(size); item.Children.Add(name);
                var status = Text("", "VideoH3.File." + row.CardId + "." + row.Path); Grid.SetColumn(status, 1); item.Children.Add(status);
                _rows.Add(row, (status, size)); list.Children.Add(item);
            }
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "VideoH3.Files"); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _status = Text("", "VideoH3.Status"); _speed = Text("", "VideoH3.Speed"); _total = Text("", "VideoH3.Total");
        footer.Children.Add(_status); footer.Children.Add(_speed); footer.Children.Add(_total);
        _progress = new() { Height = 7, Maximum = 100, Margin = new Thickness(0, 5, 0, 10) }; footer.Children.Add(_progress);
        var actions = new WrapPanel();
        actions.Children.Add(Button("H3PageBack", "VideoH3.Back", RequestBack));
        _verify = Button("H3PageVerify", "VideoH3.Verify", () => _ = RunAsync(false)); actions.Children.Add(_verify);
        _download = Button("H3PageDownload", "VideoH3.Download", () => _ = RunAsync(true)); actions.Children.Add(_download);
        _stop = Button("H3PageStop", "VideoH3.Cancel", () => _cancel?.Cancel()); actions.Children.Add(_stop);
        _next = Button("Next", "VideoH3.Next", () =>
        {
            if (!Ready || Busy) return;
            _workspaceActive = true; Build();
        });
        var navigation = new DockPanel();
        DockPanel.SetDock(_next, Dock.Right); navigation.Children.Add(_next); navigation.Children.Add(actions);
        footer.Children.Add(navigation); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root; Refresh();
    }
    internal void Configure(Func<string, string> localize) { _l = localize; Build(); }
    internal void RequestBack()
    {
        if (_workspaceActive) { if (!CanCheckpoint()) return; _workspaceActive = false; Build(); return; }
        RequestExit();
    }
    internal bool RequestExit()
    {
        if (!CanCheckpoint()) return false;
        if (Busy) { _returnAfterCancel = true; _cancel?.Cancel(); }
        else BackRequested?.Invoke();
        return true;
    }
    private void Refresh()
    {
        foreach (var (row, controls) in _rows)
        {
            controls.Status.Text = (row.Stage == "ready" ? "✓ " : row.Stage == "downloading" ? "↓ " : "○ ") + T("H3File." + row.Stage);
            controls.Size.Text = ComponentCardViewModel.FormatBytes(row.Stored) + " / " + ComponentCardViewModel.FormatBytes(row.Size);
        }
        _speed.Text = string.Format(T("H3PageSpeed"), _state.BytesPerSecond > 0
            ? ComponentCardViewModel.FormatBytes((long)_state.BytesPerSecond) + T("H3PerSecond") : "—");
        _total.Text = string.Format(T("H3PageTotal"), ComponentCardViewModel.FormatBytes(_state.StoredBytes), ComponentCardViewModel.FormatBytes(_state.TotalBytes));
        _status.Text = T(_statusKey); _progress.Value = _state.TotalBytes == 0 ? 0 : 100d * _state.StoredBytes / _state.TotalBytes;
        _progress.IsIndeterminate = Busy && _state.Rows.Any(r => r.Stage == "verifying");
        _verify.IsEnabled = !Busy; _download.IsEnabled = !Busy && !_ready; _stop.IsEnabled = Busy;
        _next.Visibility = Ready && !Busy ? Visibility.Visible : Visibility.Collapsed;
        _next.IsEnabled = Ready && !Busy;
    }
    internal async Task RunAsync(bool download)
    {
        if (Busy) return; Busy = true; _ready = false; _statusKey = download ? "H3PageDownloading" : "H3PageChecking";
        var run = ++_run; _cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken); Refresh();
        IVideoH3Preparation? preparation = null;
        try
        {
            preparation = _create();
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            var ready = await preparation.PrepareAsync(download, new Progress<ManagedModelDownloadProgress>(p =>
            {
                if (run != _run || !Busy) return;
                _state.Apply(p); _statusKey = p.Stage switch { "downloading" => "H3PageDownloading", "restarting" => "H3PageRestarting", _ => "H3PageChecking" }; Refresh();
            }), _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested(); _ready = ready; _state.Refresh(preparation.Inspect(), ready);
            _statusKey = ready ? "H3FilesReady" : "H3PageMissing";
        }
        catch (OperationCanceledException) { _statusKey = "H3PageCancelled"; if (preparation is not null) _state.Refresh(preparation.Inspect()); }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error); _statusKey = "H3PageFailed"; if (preparation is not null) _state.Fail(preparation.Inspect());
            _status.ToolTip = error.Message;
        }
        finally
        {
            preparation?.Dispose(); Busy = false; _cancel.Dispose(); _cancel = null; Refresh();
            if (_returnAfterCancel) { _returnAfterCancel = false; BackRequested?.Invoke(); }
        }
    }
}
