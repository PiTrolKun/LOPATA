using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using PasswordBox = System.Windows.Controls.PasswordBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Acquisition-only LTX page. Credentials never become project or settings data.</summary>
internal sealed class VideoLtxPreparationControl : UserControl
{
    private Func<string, string> _localize;
    private readonly string _root;
    private readonly Func<IVideoLtxPreparation> _create;
    private readonly IReadOnlyList<ManagedModelArtifactCard> _cards;
    private readonly VideoH3DownloadState _state;
    private readonly Dictionary<VideoH3FileRow, (TextBlock Status, TextBlock Size)> _rows = [];
    private CancellationTokenSource? _cancel;
    private PasswordBox? _tokenBox;
    private string _sessionToken = "";
    private string _statusKey = "LtxPageIdle";
    private TextBlock _status = new(), _speed = new(), _total = new();
    private System.Windows.Controls.ProgressBar _progress = new();
    private Button _verify = new(), _download = new(), _stop = new(), _next = new();
    private bool _checkedOnLoad, _nextStage, _exitPending, _exitHome;
    private int _run;
    internal bool Busy { get; private set; }
    internal bool Ready { get; private set; }
    internal event Action? BackRequested;
    internal event Action? HomeRequested;

    internal VideoLtxPreparationControl(Func<string, string> localize, string root, int connections,
        Func<IVideoLtxPreparation>? create = null)
    {
        _localize = localize;
        _root = root;
        _create = create ?? (() => new VideoLtxPreparation(root) { ParallelConnections = connections });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        using (var preparation = _create())
        {
            _cards = preparation.Cards;
            _state = new(_cards);
            _state.Refresh(preparation.Inspect());
        }
        AutomationProperties.SetAutomationId(this, "VideoLtx.Preparation");
        Build();
        Loaded += (_, _) =>
        {
            if (_checkedOnLoad) return;
            _checkedOnLoad = true;
            _ = RunAsync(false);
        };
        Unloaded += (_, _) => { _cancel?.Cancel(); ClearToken(); };
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
        return button;
    }

    private void Build()
    {
        if (_nextStage) { BuildNextStage(); return; }
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(Text(_localize("Navigation.Creation") + "  ›  " + _localize("Navigation.Video") + "  ›  LTX-2.5", heading: true));
        heading.Children.Add(Text(T("LtxPageTitle"), heading: true));
        heading.Children.Add(Text(T("LtxPageHint")));
        heading.Children.Add(Text(string.Format(T("H3PageStorage"), _root), "VideoLtx.Storage"));
        heading.Children.Add(Text(T("LtxAccessHint"), "VideoLtx.AccessHint"));
        var access = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        access.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        access.ColumnDefinitions.Add(new());
        var label = Text(T("LtxToken")); label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 10, 0); access.Children.Add(label);
        _tokenBox = new PasswordBox { Password = _sessionToken, MaxLength = 512, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 2, 8, 2) };
        _tokenBox.SetResourceReference(PasswordBox.ForegroundProperty, "TextPrimaryBrush");
        _tokenBox.SetResourceReference(PasswordBox.BackgroundProperty, "PanelBrush");
        _tokenBox.SetResourceReference(PasswordBox.BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(_tokenBox, "VideoLtx.AccessToken");
        AutomationProperties.SetName(_tokenBox, T("LtxToken"));
        _tokenBox.PasswordChanged += (_, _) => _sessionToken = _tokenBox.Password;
        Grid.SetColumn(_tokenBox, 1); access.Children.Add(_tokenBox);
        heading.Children.Add(access);
        var sourceActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        sourceActions.Children.Add(Action("LtxOpenModel", "VideoLtx.OpenModel", () => OpenSource("https://huggingface.co/Lightricks/LTX-2.5")));
        sourceActions.Children.Add(Action("LtxOpenTokenSettings", "VideoLtx.OpenTokenSettings", () => OpenSource("https://huggingface.co/settings/tokens")));
        heading.Children.Add(sourceActions);
        root.Children.Add(heading);

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
                var size = Text("", "VideoLtx.Size." + row.CardId + "." + row.Path); name.Children.Add(size); item.Children.Add(name);
                var status = Text("", "VideoLtx.File." + row.CardId + "." + row.Path);
                Grid.SetColumn(status, 1); item.Children.Add(status);
                _rows.Add(row, (status, size)); files.Children.Add(item);
            }
        }
        var scroll = new ScrollViewer { Content = files, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(scroll, "VideoLtx.Files");
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _status = Text("", "VideoLtx.Status"); _speed = Text("", "VideoLtx.Speed"); _total = Text("", "VideoLtx.Total");
        footer.Children.Add(_status); footer.Children.Add(_speed); footer.Children.Add(_total);
        _progress = new() { Height = 7, Maximum = 100, Margin = new Thickness(0, 5, 0, 10) }; footer.Children.Add(_progress);
        var actions = new WrapPanel();
        actions.Children.Add(Action("H3PageBack", "VideoLtx.Back", RequestBack));
        actions.Children.Add(Action("LtxPageHome", "VideoLtx.Home", RequestHome));
        _verify = Action("H3PageVerify", "VideoLtx.Verify", () => _ = RunAsync(false)); actions.Children.Add(_verify);
        _download = Action("H3PageDownload", "VideoLtx.Download", () => _ = RunAsync(true)); actions.Children.Add(_download);
        _stop = Action("H3PageStop", "VideoLtx.Cancel", () => _cancel?.Cancel()); actions.Children.Add(_stop);
        _next = Action("Next", "VideoLtx.Next", () =>
        {
            if (!Ready || Busy) return;
            ClearToken(); _nextStage = true; Build();
        });
        var navigation = new DockPanel(); DockPanel.SetDock(_next, Dock.Right);
        navigation.Children.Add(_next); navigation.Children.Add(actions); footer.Children.Add(navigation);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root; Refresh();
    }

    private void BuildNextStage()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Text(T("LtxNextTitle"), "VideoLtx.Next.Title", heading: true));
        var hint = Text(T("LtxNextHint"), "VideoLtx.Next.Hint"); Grid.SetRow(hint, 1); root.Children.Add(hint);
        var footer = new WrapPanel();
        footer.Children.Add(Action("H3PageBack", "VideoLtx.Next.Back", RequestBack));
        footer.Children.Add(Action("LtxPageHome", "VideoLtx.Next.Home", RequestHome));
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
    }
    internal void Configure(Func<string, string> localize) { _localize = localize; Build(); }
    internal bool CanCheckpoint() => true;
    internal void RequestBack()
    {
        if (_nextStage) { _nextStage = false; Build(); return; }
        RequestExit();
    }
    internal bool RequestExit()
    {
        ClearToken();
        if (Busy) { _exitPending = true; _exitHome = false; _cancel?.Cancel(); }
        else BackRequested?.Invoke();
        return true;
    }
    private void RequestHome()
    {
        ClearToken();
        if (Busy) { _exitPending = true; _exitHome = true; _cancel?.Cancel(); }
        else HomeRequested?.Invoke();
    }
    private void ClearToken() { _sessionToken = ""; _tokenBox?.Clear(); }
    private void OpenSource(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { _statusKey = "LtxPageFailed"; Refresh(); }
    }
    private void Refresh()
    {
        if (_nextStage) return;
        foreach (var (row, controls) in _rows)
        {
            controls.Status.Text = (row.Stage == "ready" ? "✓ " : row.Stage == "downloading" ? "↓ " : "○ ") + T("H3File." + row.Stage);
            controls.Size.Text = ComponentCardViewModel.FormatBytes(row.Stored) + " / " + ComponentCardViewModel.FormatBytes(row.Size);
        }
        _speed.Text = string.Format(T("H3PageSpeed"), _state.BytesPerSecond > 0
            ? ComponentCardViewModel.FormatBytes((long)_state.BytesPerSecond) + T("H3PerSecond") : "—");
        _total.Text = string.Format(T("H3PageTotal"), ComponentCardViewModel.FormatBytes(_state.StoredBytes), ComponentCardViewModel.FormatBytes(_state.TotalBytes));
        _status.Text = T(_statusKey);
        _progress.Value = _state.TotalBytes == 0 ? 0 : 100d * _state.StoredBytes / _state.TotalBytes;
        _progress.IsIndeterminate = Busy && !_state.Rows.Any(r => r.Stage is "downloading" or "assembling");
        _verify.IsEnabled = !Busy; _download.IsEnabled = !Busy && !Ready; _stop.IsEnabled = Busy;
        if (_tokenBox is not null) _tokenBox.IsEnabled = !Busy;
        _next.Visibility = Ready && !Busy ? Visibility.Visible : Visibility.Collapsed;
        _next.IsEnabled = Ready && !Busy;
    }

    internal async Task RunAsync(bool download)
    {
        if (Busy || _nextStage) return;
        Busy = true; Ready = false; _statusKey = download ? "LtxPageDownloading" : "LtxPageChecking";
        var run = ++_run;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _cancel = cancel; Refresh();
        IVideoLtxPreparation? preparation = null;
        try
        {
            preparation = _create(); preparation.AccessToken = _sessionToken;
            using var lease = ApplicationBackgroundOperations.Current?.BeginAuxiliary();
            var ready = await preparation.PrepareAsync(download, new Progress<ManagedModelDownloadProgress>(p =>
            {
                if (run != _run || !Busy) return;
                _state.Apply(p);
                _statusKey = p.Stage switch { "downloading" => "LtxPageDownloading", "restarting" => "H3PageRestarting", _ => "LtxPageChecking" };
                Refresh();
            }), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            Ready = ready; _state.Refresh(preparation.Inspect(), ready);
            _statusKey = ready ? "LtxFilesReady" : "LtxPageMissing";
        }
        catch (OperationCanceledException)
        { _statusKey = "LtxPageCancelled"; if (preparation is not null) _state.Refresh(preparation.Inspect()); }
        catch (Exception error)
        {
            var known = error.Message is "VideoPrep.LtxAccessRequired" or "VideoPrep.LtxMetadataUnavailable" or "VideoPrep.LtxMetadataInvalid";
            if (!known) ComponentAuditFailures.Report(error);
            _statusKey = known ? error.Message["VideoPrep.".Length..] : "LtxPageFailed";
            if (preparation is not null) _state.Fail(preparation.Inspect());
        }
        finally
        {
            preparation?.Dispose(); Busy = false;
            if (ReferenceEquals(_cancel, cancel)) _cancel = null;
            cancel.Dispose(); Refresh();
            if (_exitPending)
            {
                _exitPending = false;
                if (_exitHome) HomeRequested?.Invoke(); else BackRequested?.Invoke();
            }
        }
    }
}
