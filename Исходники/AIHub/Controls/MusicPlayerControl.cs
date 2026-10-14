using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed class MusicPlayerControl : UserControl, IDisposable
{
    private readonly IMusicAudioPlayer _audio;
    private readonly TextBlock _title = MusicAudioUi.Text(14), _time = MusicAudioUi.Text(), _status = MusicAudioUi.Text(11);
    private readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 1, Width = 125, IsMoveToPointEnabled = true };
    private readonly Button _play, _save, _repeat, _speaker;
    private readonly Popup _volumePopup;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Func<string, string> _l = key => key;
    private MusicTrack? _track;
    private string _errorKey = "", _outputFolder = "";
    private bool _refreshing, _disposed;
    private bool _previouslyPlaying, _playbackEnded, _surfaceActive = true;
    private bool _lyricsPending, _waitingPlay, _lyricsUnavailable;
    public event Action? PlaybackChanged;
    public event Action? PositionChanged;
    public event Action? PlaybackSeeked;
    public event Action<MusicTrack?>? TrackChanged;
    public bool IsPlaying => _audio.IsPlaying;
    public MusicTrack? CurrentTrack => _track;
    public TimeSpan PlaybackPosition => _audio.IsReady && !_disposed ? _audio.Position : TimeSpan.Zero;
    public bool CanHighlightPlayback => !_disposed && _surfaceActive && !_playbackEnded && _errorKey.Length == 0 &&
        _track is { IsExample: false } && _audio.IsReady && _audio.Duration > TimeSpan.Zero &&
        _audio.Position >= TimeSpan.Zero && _audio.Position < _audio.Duration;
    private Func<MusicTrack, bool>? _canRepeat;
    private Action<MusicTrack>? _requestRepeat;
    public void ConfigureRepeat(Func<MusicTrack, bool> canRepeat, Action<MusicTrack> request)
    { _canRepeat = canRepeat; _requestRepeat = request; Refresh(); }
    public void RefreshRepeat() { if (!_disposed) _repeat.IsEnabled = _track is { IsExample: false } && File.Exists(_track.Path) && _canRepeat?.Invoke(_track) == true; }
    public MusicPlayerControl() : this(new MusicAudioPlayer()) { }
    public MusicPlayerControl(IMusicAudioPlayer audio)
    {
        _audio = audio; AutomationProperties.SetAutomationId(this, "Music.Audio.Player");
        var root = new StackPanel { Margin = new(12, 4, 12, 4) };
        _title.FontWeight = FontWeights.SemiBold; root.Children.Add(_title);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _speaker = MusicAudioUi.IconButton("Volume", "M2,9 H6 L12,4 V20 L6,15 H2 Z M16,7 C22,10 22,14 16,17", () => _volumePopup!.IsOpen = !_volumePopup.IsOpen);
        _save = MusicAudioUi.IconButton("Save", "M12,2 V16 M7,11 L12,16 L17,11 M3,17 V22 H21 V17", Save);
        _repeat = MusicAudioUi.IconButton("Repeat", "M20,7 A9,9 0 1 0 21,16 M20,2 V7 H15 M9,8 L15,12 L9,16 Z", () => { if (_track is { } track && _canRepeat?.Invoke(track) == true) _requestRepeat?.Invoke(track); });
        _repeat.IsEnabled = false; tools.Children.Add(_speaker); tools.Children.Add(_save); tools.Children.Add(_repeat); root.Children.Add(tools);
        var popupBorder = new Border { Child = _volume, Padding = new(12), BorderThickness = new(1), CornerRadius = new(8) };
        popupBorder.SetResourceReference(BackgroundProperty, "PanelBrush"); popupBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _volumePopup = new Popup { PlacementTarget = _speaker, Placement = PlacementMode.Bottom, StaysOpen = false, Child = popupBorder, AllowsTransparency = true };
        root.Children.Add(_volumePopup);
        _volume.Value = audio.Volume; _volume.ValueChanged += (_, _) => _audio.Volume = _volume.Value;
        AutomationProperties.SetAutomationId(_volume, "Music.Audio.VolumeSlider");
        var playback = new Grid(); playback.ColumnDefinitions.Add(new() { Width = new(54) }); playback.ColumnDefinitions.Add(new());
        _play = MusicAudioUi.IconButton("Play", "M7,3 L21,12 L7,21 Z", RequestPlayback);
        _play.Width = _play.Height = 46; _play.Content = MusicAudioUi.Icon("M7,3 L21,12 L7,21 Z");
        var buttonBorder = new FrameworkElementFactory(typeof(Border)); buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(99));
        buttonBorder.SetResourceReference(BackgroundProperty, "AccentBrush");
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); buttonBorder.AppendChild(presenter);
        _play.Template = new ControlTemplate(typeof(Button)) { VisualTree = buttonBorder };
        playback.Children.Add(_play); Grid.SetColumn(_seek, 1); playback.Children.Add(_seek); root.Children.Add(playback);
        _time.Margin = new(54, 2, 0, 0); root.Children.Add(_time); _status.Margin = new(0, 4, 0, 0); root.Children.Add(_status);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextTrimming = TextTrimming.None;
        AutomationProperties.SetAutomationId(_status, "Music.Audio.Readiness");
        _seek.ValueChanged += (_, _) => { if (_refreshing) return; _playbackEnded = false; _audio.Seek(TimeSpan.FromSeconds(_seek.Value)); PlaybackSeeked?.Invoke(); RefreshTime(); };
        AutomationProperties.SetAutomationId(_seek, "Music.Audio.Seek");
        _audio.Changed += AudioChanged; _audio.Failed += Failed;
        _clock.Tick += Tick;
        Loaded += (_, _) => { if (!_disposed) { _surfaceActive = true; _clock.Start(); RefreshTime(); } };
        Unloaded += (_, _) => { _surfaceActive = false; _clock.Stop(); _volumePopup.IsOpen = false; Pause(); PositionChanged?.Invoke(); };
        Content = root; Refresh();
    }
    public void ConfigureFolder(string folder) => _outputFolder = folder;
    public void Pause() { if (!_disposed) { _waitingPlay = false; _audio.Pause(); Refresh(); } }
    public void SetLyricsPreparation(MusicTrack track, bool pending, bool unavailable = false)
    {
        if (_disposed || _track != track) return;
        _lyricsPending = pending; _lyricsUnavailable = unavailable;
        if (!pending && _waitingPlay) {
            _waitingPlay = false;
            if (_surfaceActive && _errorKey.Length == 0 && File.Exists(track.Path) && !_audio.IsPlaying) _audio.Toggle();
        }
        Refresh();
    }
    private void RequestPlayback()
    {
        if (_disposed || _track is null || _errorKey.Length > 0) return;
        if (_lyricsPending) _waitingPlay = !_waitingPlay;
        else _audio.Toggle();
        Refresh();
    }
    public void Select(MusicTrack track, bool play = false)
    {
        if (_disposed) return;
        _track = track; _errorKey = ""; _previouslyPlaying = _playbackEnded = false;
        _lyricsPending = _waitingPlay = _lyricsUnavailable = false;
        TrackChanged?.Invoke(track); _audio.Open(track.Path);
        if (play && !track.IsExample) RequestPlayback(); Refresh();
    }
    public void Localize(Func<string, string> localize)
    {
        _l = localize; MusicAudioUi.Label(_speaker, _l("Music.Audio.Volume")); MusicAudioUi.Label(_save, _l("Music.Audio.Save"));
        MusicAudioUi.Label(_repeat, _l("Music.Audio.Repeat"));
        AutomationProperties.SetName(_seek, _l("Music.Audio.Seek")); AutomationProperties.SetName(_volume, _l("Music.Audio.Volume")); Refresh();
    }
    private void AudioChanged() {
        if (_audio.IsPlaying) _playbackEnded = false;
        else if (_previouslyPlaying && _audio.Position == TimeSpan.Zero) _playbackEnded = true;
        _previouslyPlaying = _audio.IsPlaying; Refresh(); PlaybackChanged?.Invoke();
    }
    public void Clear() { _track = null; _errorKey = ""; _lyricsPending = _waitingPlay = _lyricsUnavailable = false; TrackChanged?.Invoke(null); _audio.Open(null); Refresh(); }
    private void Failed(string key) { _waitingPlay = false; _errorKey = key; Refresh(); }
    private void Tick(object? sender, EventArgs e) => RefreshTime();
    private void Refresh()
    {
        if (_disposed) return;
        _title.Text = _track is null ? _l("Music.Audio.NoTrack") : _track.IsExample ? _l(_track.Title) : _track.Title; _title.ToolTip = _title.Text;
        _status.Text = _errorKey.Length > 0 ? _l(_errorKey) : _track?.IsExample == true ? _l("Music.Audio.ExampleHint") :
            _lyricsPending ? _l(_waitingPlay ? "Music.Audio.SyncWaitingPlay" : "Music.Audio.SyncPending") :
            _lyricsUnavailable ? _l("Music.Audio.KaraokeUnavailable") : "";
        var hasFile = _track is { IsExample: false } && File.Exists(_track.Path);
        _save.IsEnabled = hasFile; _play.IsEnabled = hasFile && _errorKey.Length == 0;
        RefreshRepeat();
        _play.Opacity = _play.IsEnabled ? 1 : .45;
        _play.Content = MusicAudioUi.Icon(_audio.IsPlaying || _waitingPlay ? "M7,3 V21 M17,3 V21" : "M7,3 L21,12 L7,21 Z");
        MusicAudioUi.Label(_play, _l(_waitingPlay ? "Music.Audio.CancelWaitingPlay" : _audio.IsPlaying ? "Music.Audio.Pause" : "Music.Audio.Play"));
        _seek.IsEnabled = _track is not null && _audio.IsReady; RefreshTime();
    }
    private void RefreshTime()
    {
        if (_disposed) return;
        _refreshing = true;
        if (_track is null) { _seek.Value = 0; _time.Text = "00:00 / 00:00"; _refreshing = false; PositionChanged?.Invoke(); return; }
        _seek.Maximum = Math.Max(1, (_audio.IsReady ? _audio.Duration : _track?.Duration ?? TimeSpan.Zero).TotalSeconds);
        _seek.Value = _audio.IsReady ? _audio.Position.TotalSeconds : 0;
        _refreshing = false;
        _time.Text = MusicAudioUi.Time(_audio.Position) + " / " + MusicAudioUi.Time(_audio.IsReady ? _audio.Duration : _track?.Duration ?? TimeSpan.Zero);
        PositionChanged?.Invoke();
    }
    private void Save()
    {
        if (_track is null || _track.IsExample || !File.Exists(_track.Path)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = _l("Music.Audio.Save"), FileName = Path.GetFileName(_track.Path),
            Filter = $"{_l("Music.Audio.File")} (*{Path.GetExtension(_track.Path)})|*{Path.GetExtension(_track.Path)}", InitialDirectory = Directory.Exists(_outputFolder) ? _outputFolder : "" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            MusicTrackFiles.Copy(_track.Path!, dialog.FileName);
            _status.Text = _l("Music.Audio.Saved");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _status.Text = _l("Music.Audio.FileError") + " " + e.Message; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _waitingPlay = false; _clock.Stop(); _clock.Tick -= Tick; _volumePopup.IsOpen = false;
        _audio.Changed -= AudioChanged; _audio.Failed -= Failed; _audio.Dispose();
        PositionChanged?.Invoke(); PositionChanged = null;
        PlaybackSeeked = null;
        TrackChanged = null;
    }
}
