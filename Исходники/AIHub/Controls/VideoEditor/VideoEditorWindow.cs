using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly VideoWork _work;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _localOperationCancel;
    private CancellationToken MediaToken => _localOperationCancel?.Token ?? _lifetime.Token;
    private VideoEditing _editing;
    private VideoProject Project => _editing.Project;
    private readonly VideoPreview _preview = new();
    private readonly VideoTimeline _timeline = new() { Focusable = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _clock = new() { Margin = new(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _properties = new();
    private readonly ListBox _materials = new() { DisplayMemberPath = "Name" };
    private readonly VideoSegmentList _segments = new() { DisplayMemberPath = "Caption", Height = 110 };
    private readonly TextBox _script = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 80 };
    private readonly TextBox _brief = new() { MinHeight = 35 };
    private readonly CheckBox _subtitles = new() { IsChecked = true };
    private readonly CheckBox _follow = new() { IsChecked = true };
    private readonly CheckBox _framing = new();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 4 };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _language;
    private string? _path, _selectedSegment;
    private bool _busy, _playing, _loadingText, _dirty, _closed;
    private double _position;
    private string? _operationId;
    internal Func<Task>? EnsureRuntime { get; set; }
    internal string ProjectId => Project.Id;
    internal void OpenProjectPath(string path) => Safe(async () => await LoadProjectAsync(path));
    internal void FollowOperation(string id) { _operationId = id; _busy = true; }
    internal async Task AcceptOperationResultAsync(string path)
    {
        if (_closed || !path.EndsWith(".lopataVideo", StringComparison.OrdinalIgnoreCase)) return;
        var next = VideoProjectStore.Load(path); if (next.Id != Project.Id) return;
        _saveChanges.Replaced(_editing.Revision); _editing = new(next); _busy = false; CloseSvgProgress(); _dirty = true; await PrepareMediaAsync(); await RefreshAsync(); UpdateTitle();
    }
    internal event Action<string>? EditSvgRequested;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal VideoEditorWindow(Window owner, Func<string, string> l, string language, VideoWork work, VideoProject? project = null, string? path = null)
    {
        Owner = owner; _l = l; _language = language; _work = work; _editing = new(project ?? new()); _path = path;
        if (project is null) { Project.Name = T("Untitled"); foreach (var t in Project.Tracks) t.Name = T(t.Kind == "video" ? "VideoTrack" : "AudioTrack") + " 1"; }
        _saveChanges = new(Project, path is not null);
        MusicWishUi.PrepareWindow(this, T("Title"), "VideoEditor.Window", 1500); Height = 950; MinWidth = 1050; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/VideoEditor/VideoEditorResources.xaml", UriKind.Relative) });
        _segments.SetResourceReference(StyleProperty, typeof(System.Windows.Controls.ListBox));
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _timeline.SeriesText = T; BuildLayout(); UpdateTitle();
        _timeline.SubtitleLabel = T("SubtitleTrack"); _timeline.EmptySubtitleLabel = T("EmptySubtitle");
        _timeline.SelectionChanged += (_, _) => TimelineSelectionChanged(); _timeline.SeekRequested += at => Safe(async () => await SeekAsync(at));
        _timeline.EditRequested += action => Edit(action);
        _timeline.VolumeRequested += (track, clip, anchor) => Safe(async () => await OpenVolumeAsync(track, clip, anchor)); _timeline.HelpText = key => VideoEditorCommands.Tip(key, T);
        _preview.PositionChanged += at => { if (!_playing) return; _position = at; if (at >= Project.Duration) _playing = false; UpdatePosition(); };
        _script.IsEnabled = false; _segments.SelectionChanged += (_, e) => SegmentSelectionChanged(e); _script.TextChanged += (_, _) => ScriptChanged();
        _segments.MouseDoubleClick += (_, _) => { if (_segments.SelectedItem is NarrationSegment n) Safe(async () => await SeekAsync(n.Start)); };
        var fragmentMenu = new ContextMenu(); var timing = new MenuItem { Header = T("FragmentSettings"), ToolTip = VideoEditorCommands.Tip("FragmentSettings", T) }; timing.Click += (_, _) => FragmentSettings(); fragmentMenu.Items.Add(timing); _segments.ContextMenu = fragmentMenu;
        var automatic = new MenuItem { Header = T("SubtitleAutomaticTiming"), ToolTip = VideoEditorCommands.Tip("SubtitleAutomaticTiming", T) }; automatic.Click += (_, _) => { var ids = SelectedFragments(); Edit(p => VideoSubtitles.ResetRanges(p, ids)); }; fragmentMenu.Items.Add(automatic);
        _subtitles.Click += (_, _) => Safe(RefreshPreviewAsync);
        _framing.Click += (_, _) => Safe(UpdateFramingAsync); _preview.FramingChanged += ApplyFraming;
        _preview.ResizeLabel = T("ResizeHandle"); _preview.RotateLabel = T("RotateHandle");
        _preview.FramingSelected += SelectFramingClip;
        _preview.FramingGesture += active => { _saveTimer.Stop(); if (!active && _dirty) _saveTimer.Start(); };
        _preview.ExpandedRequested += expanded => Safe(() => SetPreviewExpandedAsync(expanded));
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); try { VideoProjectStore.Save(RecoveryProject, VideoProjectStore.Recovery(Project.Id)); _segments.Items.Refresh(); _timeline.Refresh(); Safe(RefreshPreviewAsync); } catch (Exception e) { _status.Text = T("RecoveryFailed") + " " + Error(e); } };
        _work.Changed += WorkChanged; _work.SvgChanged += SvgChanged;
        Loaded += (_, _) => Safe(async () => { await _preview.InitializeAsync(_lifetime.Token); await _preview.SetExpandedAsync(false, T("ExpandPreview")); await PrepareMediaAsync(); await RefreshAsync(); Ready.TrySetResult(); });
        Closed += (_, _) => { _closed = true; CloseVolumePopup(); _lifetime.Cancel(); _saveTimer.Stop(); _preview.Dispose(); CloseSvgProgress(); _work.Changed -= WorkChanged; _work.SvgChanged -= SvgChanged; };
        Closing += EditorClosing;
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape && PreviewExpanded) { e.Handled = true; Safe(() => SetPreviewExpandedAsync(false)); return; }
            if (Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Space) { e.Handled = true; Safe(ToggleAsync); }
            else if (e.Key == Key.Delete) { e.Handled = true; DeleteClip(); }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save(false); }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Undo(false); }
            else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Undo(true); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyClips(); }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; PasteClips(); }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (_segments.IsKeyboardFocusWithin) _segments.SelectAll(); else _timeline.SelectAll(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _segments.EndBox(); _timeline.CancelSelection(); }
        };
    }
    private string T(string key) => _l("VideoEditor." + key);
    private string Error(Exception e)
    {
        var split = e.Message.IndexOf(':'); var key = split < 0 ? e.Message : e.Message[..split];
        return key.StartsWith("VideoEditor.", StringComparison.Ordinal) || key.StartsWith("Vector.", StringComparison.Ordinal)
            ? _l(key) + (split < 0 ? "" : e.Message[split..]) : e.Message;
    }
    private async void Safe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = T("Interrupted"); }
        catch (Exception error) { VideoErrors.Report(error); if (!_closed) _status.Text = Error(error); Ready.TrySetResult(); }
    }
    private Button B(string key, Action action)
    {
        var button = new Button { Content = T(key), ToolTip = VideoEditorCommands.Tip(key, T), Padding = new(9, 6, 9, 6), Margin = new(0, 0, 4, 4) };
        if (VideoEditorCommands.Icon(key, button) is {} icon) { button.Content = icon; button.Width = button.MinWidth = 36; button.Height = button.MinHeight = 34; button.Padding = new(7); }
        if (key == "Play") _playButton = button;
        AutomationProperties.SetName(button, T(key)); ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetAutomationId(button, "VideoEditor." + key); button.Click += (_, _) => { try { action(); } catch (Exception e) { _status.Text = Error(e); } }; return button;
    }
    private void Edit(Action<VideoProject> action)
    {
        if (_busy) { _status.Text = T("Busy"); return; }
        try { _editing.Edit(action); _dirty = true; _saveTimer.Stop(); _saveTimer.Start(); UpdateTitle(); Safe(RefreshAsync); }
        catch (Exception e) { _status.Text = Error(e); Safe(RefreshAsync); }
    }
    private void UpdateTitle() => Title = T("Title") + " · " + Project.Name + (_dirty ? " *" : "");
    private async Task RefreshAsync()
    {
        RefreshSelectionControls();
        _timeline.Project = Project; _timeline.Refresh(); _materials.ItemsSource = Project.Assets.ToArray();
        _changingTrack = true;
        var tracks = Project.Narration.GroupBy(TrackKey).Select(g => new KeyValuePair<string,string>(g.Key, g.First().Language + " · " + g.First().SubtitleTitle)).ToArray();
        _subtitleTracks.ItemsSource = tracks;
        if (!tracks.Any(t => t.Key == _subtitleFilter)) _subtitleFilter = Project.Narration.FirstOrDefault(n => n.SubtitleDefault) is {} preferred ? TrackKey(preferred) : tracks.FirstOrDefault().Key;
        _subtitleTracks.SelectedValue = _subtitleFilter; _changingTrack = false;
        var selected = _selectedSegment; var selectedIds = _segments.SelectedItems.Cast<NarrationSegment>().Select(n => n.Id).ToHashSet();
        _loadingText = true; _syncSelection = true; _segments.ItemsSource = Project.Narration.OrderBy(n => n.DisplayStart ?? n.SubtitleStart ?? n.Start).ToArray();
        _segments.SelectedItems.Clear();
        foreach (var n in Project.Narration.Where(n => _timeline.Selection.Roots.Count == 0 && selectedIds.Contains(n.Id) || _timeline.Selection.Contains(new(n.Id, true)))) _segments.SelectedItems.Add(n);
        if (_segments.SelectedItems.Count == 0 && _timeline.Selection.Count == 0) _segments.SelectedItem = Project.Narration.FirstOrDefault(n => n.Id == selected) ?? Project.Narration.FirstOrDefault(n => _position >= n.Start && _position < n.End);
        _syncSelection = false; _loadingText = false;
        LoadSelectedText();
        if (_timeline.Selection.Count == 0 && _segments.SelectedItems.Count > 0) _timeline.SelectItems(_segments.SelectedItems.Cast<NarrationSegment>().Select(n => new VideoItemId(n.Id, true)).ToArray());
        ShowProperties(); await RefreshPreviewAsync(); UpdatePosition();
    }
    private async Task RefreshPreviewAsync() { await _preview.LoadAsync(Project, _position = Math.Min(_position, Project.Duration), _subtitles.IsChecked == true, _subtitleFilter, FramingClipId(), _framing.IsChecked == true && !_busy); if (_playing) await _preview.PlayAsync(true); }
    private async Task PrepareMediaAsync()
    {
        using var audit = ComponentAudit.CaptureOperation(); await VideoRuntime.PrepareAsync(MediaToken);
        VideoProjectStore.VerifySources(Project);
        foreach (var asset in Project.Assets)
        {
            _status.Text = T("Preparing") + " " + asset.Name;
            await VideoPreviewAssets.PrepareAsync(Project, asset, MediaToken);
            var dir = VideoPreviewAssets.DirectoryFor(Project);
            if (asset.Kind != "audio") { var path = await VideoImport.ThumbnailAsync(asset, dir, MediaToken); if (File.Exists(path)) { var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); _timeline.Thumbnails[asset.Id] = bitmap; } }
            if (asset.AudioStream >= 0) _timeline.Waveforms[asset.Id] = await VideoPreviewAssets.WaveformAsync(asset, dir, MediaToken);
        }
        _status.Text = T("Ready");
    }
    private async Task SeekAsync(double at) { _position = Math.Clamp(at, 0, Project.Duration); await _preview.SeekAsync(_position); UpdatePosition(); }
    private async Task ToggleAsync() { if (!_playing && _position >= Project.Duration) await SeekAsync(0); _playing = !_playing; await _preview.PlayAsync(_playing); UpdateVoiceSummary(); }
    private void UpdatePosition()
    {
        UpdatePlayIcon();
        _timeline.Position = _position; _timeline.InvalidateVisual(); _clock.Text = TimeSpan.FromSeconds(_position).ToString(@"hh\:mm\:ss\.fff") + " / " + TimeSpan.FromSeconds(Project.Duration).ToString(@"hh\:mm\:ss");
        HighlightNarration();
        UpdateVoiceSummary();
    }
    private void WorkChanged(VideoWorkProgress progress)
    {
        if (progress.Id != _operationId || _closed) return;
        _ = Dispatcher.InvokeAsync(() => {
            if (_closed) return; _progress.Value = progress.Fraction; _status.Text = progress.Error is null ? _l(progress.Status) : Error(new Exception(progress.Error));
            if (progress.Status.StartsWith("Vector.", StringComparison.Ordinal)) _svgLive?.SetStage(progress.Status[7..]);
        });
    }
}
