using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

internal sealed partial class VideoVaceWorkspaceControl
{
    private Border _canvas = new();
    private Grid _previewHost = new();
    private MediaElement _player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close };
    private System.Windows.Controls.Button _play = new();
    private TextBlock _empty = new(), _time = new();
    private bool _playing;
    private string? _playingPath;
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UIElement BuildPreview()
    {
        var root = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(Label(T("ResultPreview")));
        _empty = Label(T("EmptyPreview")); _empty.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center; _empty.TextAlignment = TextAlignment.Center; _empty.Margin = new Thickness(16); _empty.Foreground = Brushes.LightGray;
        AutomationProperties.SetAutomationId(_empty, "VideoVace.Workspace.EmptyPreview");
        var media = new Grid(); _player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true, Stretch = System.Windows.Media.Stretch.Uniform };
        AutomationProperties.SetAutomationId(_player, "VideoVace.Workspace.Player"); media.Children.Add(_player); media.Children.Add(_empty);
        _player.MediaOpened += (_, _) => { _play.IsEnabled = true; ResizeCanvas(); DrawPlaybackTime(); };
        _player.MediaEnded += (_, _) => { _playing = false; _playTimer.Stop(); _player.Position = TimeSpan.Zero; _play.Content = "▶"; };
        _player.MediaFailed += (_, _) => { _play.IsEnabled = false; Notify(T("PlaybackFailed")); };
        _playTimer.Tick -= PlaybackTick; _playTimer.Tick += PlaybackTick;
        _canvas = new Border { Background = Brushes.Black, Child = media, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _previewHost = new Grid { MinHeight = 100 }; _previewHost.Children.Add(_canvas); _previewHost.SizeChanged += (_, _) => ResizeCanvas();
        Grid.SetRow(_previewHost, 1); root.Children.Add(_previewHost);
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        _play = ActionButton("PlayPreview", "PlayPreview", TogglePlayback); _play.Content = "▶"; _play.IsEnabled = false; _play.ToolTip = T("PlayPreview"); actions.Children.Add(_play);
        var expand = ActionButton("ExpandPreview", "ExpandPreview", TogglePreview); expand.Content = "⛶"; expand.ToolTip = T("ExpandPreview"); actions.Children.Add(expand);
        _time = Label("00:00:00 / 00:00:00"); actions.Children.Add(_time); Grid.SetRow(actions, 2); root.Children.Add(actions); return root;
    }
    private void SelectResult()
    {
        var path = SelectedVideo; if (path == _playingPath) return;
        ClosePlayback(); _playingPath = path; ResizeCanvas();
        if (path is null) { _empty.Visibility = Visibility.Visible; return; }
        try
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) throw new FileNotFoundException();
            _player.Source = new Uri(Path.GetFullPath(path)); _player.Play(); _player.Pause(); _empty.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { Notify(T("PlaybackFailed")); }
    }
    private void TogglePlayback()
    {
        if (_player.Source is null) return;
        if (_playing) { _player.Pause(); _playTimer.Stop(); _play.Content = "▶"; }
        else { _player.Play(); _playTimer.Start(); _play.Content = "Ⅱ"; }
        _playing = !_playing;
    }
    private void DrawPlaybackTime() => _time.Text = _player.Position.ToString(@"hh\:mm\:ss") + " / " +
        (_player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero).ToString(@"hh\:mm\:ss");
    private void PlaybackTick(object? sender, EventArgs args) => DrawPlaybackTime();
    private void ClosePlayback() { _playTimer.Stop(); _player.Close(); _playing = false; _playingPath = null; _play.Content = "▶"; _play.IsEnabled = false; _time.Text = "00:00:00 / 00:00:00"; }
    private void ResizeCanvas()
    {
        var ratio = _player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0 ? _player.NaturalVideoWidth / (double)_player.NaturalVideoHeight : S.Width / (double)S.Height;
        _canvas.Width = Math.Max(0, Math.Min(_previewHost.ActualWidth, _previewHost.ActualHeight * ratio)); _canvas.Height = _canvas.Width / ratio;
    }
    private void TogglePreview()
    {
        _expanded = !_expanded;
        foreach (var column in new[] { 0, 1, 3, 4 })
        {
            _body.ColumnDefinitions[column].Width = _expanded ? new GridLength(0) : column is 1 or 3 ? new GridLength(6) : new GridLength(28, GridUnitType.Star);
            foreach (UIElement child in _body.Children) if (Grid.GetColumn(child) == column) child.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
