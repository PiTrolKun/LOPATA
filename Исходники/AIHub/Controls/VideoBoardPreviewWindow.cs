using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class VideoBoardPreviewWindow : Window
{
    private readonly VideoPreparationProject _project;
    private readonly Func<string, string> _localize;
    private readonly IMusicAudioPlayer _audio;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _watch = new();
    private readonly Image _image = new() { Height = 370, Stretch = System.Windows.Media.Stretch.Uniform };
    private readonly TextBlock _title = VideoReferenceWindowUi.Text(""), _action = VideoReferenceWindowUi.Text(""), _time = VideoReferenceWindowUi.Text("");
    private readonly Slider _seek = new() { Minimum = 0, TickFrequency = .1, Margin = new Thickness(4, 10, 4, 10) };
    private double _position, _base;
    private string _frame = "";
    private bool _updating, _waiting, _soundFailed;
    public double Position => _position;
    public bool IsPlaying => _watch.IsRunning;
    public VideoBoardPreviewWindow(Window? owner, Func<string, string> localize, VideoPreparationProject p, IMusicAudioPlayer? audio = null)
    {
        _project = p.Clone(); _localize = localize; _audio = audio ?? new MusicAudioPlayer();
        VideoReferenceWindowUi.Configure(this, owner, T("BoardPreview"));
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "VideoPrep.BoardPreview.Window");
        var page = new StackPanel { Margin = new Thickness(16) };
        page.Children.Add(VideoReferenceWindowUi.Text(T("BoardPreviewHint"))); page.Children.Add(_image); page.Children.Add(_title); page.Children.Add(_action);
        _seek.Maximum = Math.Max(.01, VideoStoryboard.Duration(_project)); page.Children.Add(_seek);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_seek, "VideoPrep.BoardPreview.Seek");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(VideoReferenceWindowUi.Button(T("BoardPlayPause"), "BoardPreview.Play", () => { if (_watch.IsRunning || _waiting) Pause(); else Play(); }));
        buttons.Children.Add(VideoReferenceWindowUi.Button(T("BoardStop"), "BoardPreview.Stop", () => { Pause(); Seek(0); }));
        buttons.Children.Add(_time); page.Children.Add(buttons);
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _seek.ValueChanged += (_, _) => { if (!_updating) Seek(_seek.Value); };
        _timer.Tick += (_, _) => { _position = Math.Min(_seek.Maximum, _base + _watch.Elapsed.TotalSeconds); Draw(); if (_position >= _seek.Maximum) Pause(); };
        _audio.Changed += () => { if (_waiting && _audio.IsReady) { _waiting = false; Play(); } };
        _audio.Failed += _ => { _soundFailed = true; _waiting = false; _time.Text = T("BoardAudioPlaybackFailed"); };
        var path = VideoStoryboard.AudioPath(_project); if (path is not null) _audio.Open(path);
        Closed += (_, _) => { _timer.Stop(); _watch.Stop(); _audio.Dispose(); };
        Draw();
    }
    private string T(string key) => _localize("VideoPrep." + key);
    public void Play()
    {
        if (VideoStoryboard.Duration(_project) <= 0) return;
        if (VideoStoryboard.AudioPath(_project) is not null && !_soundFailed && !_audio.IsReady)
        { _waiting = true; _time.Text = T("BoardAudioPreparing"); return; }
        if (_position >= _seek.Maximum) _position = 0;
        _base = _position; _watch.Restart(); _timer.Start();
        if (_audio.IsReady && _position < _audio.Duration.TotalSeconds) { _audio.Seek(TimeSpan.FromSeconds(_position)); if (!_audio.IsPlaying) _audio.Toggle(); }
    }
    public void Pause() { _waiting = false; if (_watch.IsRunning) _position = Math.Min(_seek.Maximum, _base + _watch.Elapsed.TotalSeconds); _base = _position; _watch.Reset(); _timer.Stop(); _audio.Pause(); Draw(); }
    public void Seek(double seconds)
    {
        _position = Math.Clamp(seconds, 0, _seek.Maximum); _base = _position; if (_watch.IsRunning) _watch.Restart();
        if (_audio.IsReady) _audio.Seek(TimeSpan.FromSeconds(Math.Min(_position, _audio.Duration.TotalSeconds))); Draw();
    }
    private void Draw()
    {
        _updating = true; _seek.Value = _position; _updating = false;
        _time.Text = $"{TimeSpan.FromSeconds(_position):hh\\:mm\\:ss\\.fff} / {TimeSpan.FromSeconds(_seek.Maximum):hh\\:mm\\:ss\\.fff}";
        if (VideoStoryboard.At(_project, _position).Frame is not { } f || _frame == f.Id) return; _frame = f.Id;
        var pair = VideoStoryboard.Plans(_project).FirstOrDefault(x => x.Shot.Id == f.ShotId);
        _title.Text = pair.Shot is null ? T("BoardMissingPlan") : pair.Scene.Name + " · " + pair.Shot.Name;
        _action.Text = pair.Shot is null ? f.Notes : pair.Shot.Start + "\n→ " + pair.Shot.End + (f.Notes.Length > 0 ? "\n" + f.Notes : "");
        _image.Source = null;
        if (_project.References?.Images.FirstOrDefault(i => i.Id == f.ImageId) is { } image)
            try { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1600;
                bitmap.UriSource = new Uri(VideoReferences.PathFor(_project, image)); bitmap.EndInit(); bitmap.Freeze(); _image.Source = bitmap; }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException) { _title.Text += " · " + T("BoardMissingImage"); }
    }
}
