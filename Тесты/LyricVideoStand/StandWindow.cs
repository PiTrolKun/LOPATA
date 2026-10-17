using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LyricVideoStand;

internal sealed class StandWindow : Window
{
    private readonly StandConfig _config;
    private readonly StandLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
    private readonly MediaPlayer _player = new();
    private readonly Stopwatch _creditsClock = new();
    private readonly Stopwatch _diagnostics = Stopwatch.StartNew();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _logs = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(9, 17, 22)), Foreground = new SolidColorBrush(Color.FromRgb(149, 184, 166)), FontSize = 12, BorderThickness = new Thickness(0) };
    private readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, Margin = new Thickness(16, 0, 16, 0) };
    private readonly TextBlock _status = new() { Text = "Подготовка песни и анализ…", Foreground = Brushes.LightGray, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _clock = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.LightGray };
    private readonly Button _play, _export, _cancel;
    private readonly ProgressBar _progress = new() { Height = 3, Maximum = 1, Margin = new Thickness(0, 9, 0, 0), Foreground = Brushes.MediumSeaGreen };
    private StandSession? _session;
    private SceneRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private Task? _loadWork, _frameWork, _exportWork;
    private CancellationTokenSource? _exportCancellation;
    private bool _ready, _playing, _credits, _closing, _allowClose, _updatingSeek, _dragging;
    private double _creditsBase, _lastFrame = -1;
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StandWindow(StandConfig config, StandLog log)
    {
        _config = config; _log = log; Title = "ЛОПАТА — стенд музыкального клипа";
        Width = 1500; Height = 945; MinWidth = 1050; MinHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(14, 21, 32)); Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI");
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 15) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Стенд музыкального клипа", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "1920 × 1080 · 30 кадров/с · " + config.Version + " · проверка механики", Foreground = Brushes.Gray, Margin = new Thickness(0, 5, 0, 0) });
        var bottom = new StackPanel { Margin = new Thickness(0, 15, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var transport = new DockPanel(); bottom.Children.Add(transport);
        _play = Button("▶ Воспроизвести", (_, _) => Toggle()); _play.IsEnabled = false;
        transport.Children.Add(_play);
        transport.Children.Add(Button("↶ В начало", (_, _) => { if (_ready) Seek(0); }));
        _cancel = Button("Отменить экспорт", (_, _) => _exportCancellation?.Cancel()); _cancel.IsEnabled = false;
        DockPanel.SetDock(_cancel, Dock.Right); transport.Children.Add(_cancel);
        _export = Button("Экспорт MP4", async (_, _) => await ExportAsync()); _export.IsEnabled = false;
        DockPanel.SetDock(_export, Dock.Right); transport.Children.Add(_export);
        DockPanel.SetDock(_clock, Dock.Right); transport.Children.Add(_clock); transport.Children.Add(_seek);
        bottom.Children.Add(_progress); bottom.Children.Add(_status);
        var content = new Grid(); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = new GridLength(305) }); root.Children.Add(content);
        var frame = new Border { Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromRgb(44, 65, 57)), BorderThickness = new Thickness(1), Child = _image };
        content.Children.Add(frame);
        var diagnosticPanel = new DockPanel { Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(diagnosticPanel, 1); content.Children.Add(diagnosticPanel);
        var diagnosticTitle = new TextBlock { Text = "Диагностика", FontSize = 17, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(diagnosticTitle, Dock.Top); diagnosticPanel.Children.Add(diagnosticTitle); diagnosticPanel.Children.Add(_logs);
        _log.Message += OnLog;
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; Seek(_seek.Value); }));
        _seek.ValueChanged += (_, _) => { if (!_updatingSeek && !_dragging && _ready) Seek(_seek.Value); };
        _timer.Tick += (_, _) => RequestFrame();
        _player.MediaOpened += (_, _) => { _ready = true; _play.IsEnabled = _export.IsEnabled = true; RequestFrame(); Ready.TrySetResult(); };
        _player.MediaEnded += (_, _) => { _credits = true; _creditsBase = _session!.SongDuration; _creditsClock.Restart(); };
        _player.MediaFailed += (_, args) => { _ready = false; _log.Write("Playback failure: " + args.ErrorException.Message); _status.Text = "Не удалось открыть звук."; Ready.TrySetException(args.ErrorException); };
        Loaded += async (_, _) => await LoadAsync(); Closing += OnClosing;
    }
    private static Button Button(string caption, RoutedEventHandler handler)
    {
        var button = new Button { Content = caption, Padding = new Thickness(13, 9, 13, 9), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(29, 46, 45)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 82, 71)), BorderThickness = new Thickness(1) };
        button.Click += handler; return button;
    }
    private async Task LoadAsync()
    {
        try
        {
            var loading = Task.Run(() => StandSession.LoadAsync(_config, _log, _lifetime.Token)); _loadWork = loading;
            _session = await loading; if (_closing) return;
            _renderer = new SceneRenderer(_session); _bitmap = new WriteableBitmap(1920, 1080, 96, 96, PixelFormats.Bgra32, null);
            _log.Write($"Weather: drizzle-to-storm mapping; 3 rain layers; {_renderer.LyricRainWordCount} source lyric words/fragments in background.");
            _image.Source = _bitmap; _seek.Maximum = _session.TotalDuration; _player.Volume = .7;
            _status.Text = _session.Artist + " — " + _session.Title + ". Готово к просмотру.";
            _player.Open(new Uri(_session.WavePath)); _timer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) { _log.Write(error.ToString()); _status.Text = "Ошибка подготовки: " + error.Message; Ready.TrySetException(error); } }
    }
    private double Time => _credits ? _creditsBase + (_playing ? _creditsClock.Elapsed.TotalSeconds : 0) : _player.Position.TotalSeconds;
    private void Toggle()
    {
        if (!_ready || _session is null) return;
        if (Time >= _session.TotalDuration - .02) Seek(0);
        if (_playing)
        {
            if (_credits) { _creditsBase = Time; _creditsClock.Reset(); } else _player.Pause();
        }
        else { if (_credits) _creditsClock.Restart(); else _player.Play(); }
        _playing = !_playing; _play.Content = _playing ? "Ⅱ Пауза" : "▶ Воспроизвести";
    }
    private void Seek(double position)
    {
        if (!_ready || _session is null) return;
        position = Math.Clamp(position, 0, _session.TotalDuration); _player.Pause(); _creditsClock.Reset();
        _credits = position >= _session.SongDuration;
        if (_credits) { _creditsBase = position; if (_playing) _creditsClock.Start(); }
        else { _player.Position = TimeSpan.FromSeconds(position); if (_playing) _player.Play(); }
        _lastFrame = -1; RequestFrame();
    }
    private void RequestFrame()
    {
        if (!_ready || _session is null || _renderer is null || _closing || _frameWork is { IsCompleted: false }) return;
        var t = Math.Clamp(Time, 0, _session.TotalDuration);
        if (t >= _session.TotalDuration && _playing)
        {
            _playing = false; _creditsBase = _session.TotalDuration; _creditsClock.Reset();
            _play.Content = "▶ Воспроизвести";
        }
        if (Math.Abs(t - _lastFrame) < 1d / 120) return;
        _lastFrame = t; _frameWork = DrawAsync(t);
    }
    private async Task DrawAsync(double time)
    {
        try
        {
            var watch = Stopwatch.StartNew(); var pixels = await Task.Run(() => _renderer!.Render(time));
            if (_closing) return;
            _bitmap!.WritePixels(new Int32Rect(0, 0, 1920, 1080), pixels, 1920 * 4, 0);
            _clock.Text = Format(time) + " / " + Format(_session!.TotalDuration);
            if (!_dragging) { _updatingSeek = true; _seek.Value = time; _updatingSeek = false; }
            if (_diagnostics.ElapsedMilliseconds > 1000)
            {
                var a = _session.Analysis.At(time); _diagnostics.Restart();
                var weather = _renderer!.WeatherAt(time);
                _log.Write($"t={time:F2}s stage={_session.Stage(time)} level={a.Level:F2} bass={a.Bass:F2} storm={weather.Storm:F2} wave={weather.WaveHeight:F0}px render={watch.Elapsed.TotalMilliseconds:F1}ms");
            }
        }
        catch (Exception error) { if (!_closing) { _timer.Stop(); _player.Pause(); _log.Write(error.ToString()); _status.Text = "Ошибка отрисовки: " + error.Message; } }
    }
    private async Task ExportAsync()
    {
        if (_session is null || _exportWork is { IsCompleted: false }) return;
        if (_playing) Toggle(); _export.IsEnabled = false; _cancel.IsEnabled = true;
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var path = Path.Combine(_config.Output, "clip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".mp4");
        try
        {
            var progress = new Progress<double>(p => { if (!_closing) { _progress.Value = p; _status.Text = "Экспорт: " + (p * 100).ToString("F0") + "%"; } });
            _exportWork = Task.Run(() => VideoExport.RunAsync(_session, path, 0, _session.TotalDuration, _log, ((IProgress<double>)progress).Report, _exportCancellation.Token));
            await _exportWork;
            if (!_closing) { _status.Text = "Готовый файл: " + path; _log.Write("Saved: " + path); }
        }
        catch (OperationCanceledException) { if (!_closing) _status.Text = "Экспорт отменён. Исходная песня сохранена."; }
        catch (Exception error) { if (!_closing) { _status.Text = "Ошибка экспорта: " + error.Message; _log.Write(error.ToString()); } }
        finally { _exportCancellation.Dispose(); _exportCancellation = null; if (!_closing) { _export.IsEnabled = true; _cancel.IsEnabled = false; } }
    }
    private void OnLog(string line)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() => {
            if (_closing) return;
            if (_logs.Text.Length > 24000) _logs.Text = _logs.Text[^16000..];
            _logs.AppendText(line + Environment.NewLine); _logs.ScrollToEnd();
        });
    }
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; if (_closing) return;
        _closing = true; _timer.Stop(); _lifetime.Cancel(); _player.Close();
        try { await Task.WhenAll(new[] { _loadWork, _frameWork, _exportWork }.OfType<Task>()); } catch (Exception) { /* Logged by the owning operation. */ }
        _renderer?.Dispose(); _log.Message -= OnLog; _lifetime.Dispose(); _allowClose = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }
    internal async Task SmokeAsync()
    {
        await Ready.Task.WaitAsync(TimeSpan.FromSeconds(90));
        _player.Volume = 0; Seek(30); Toggle(); await Task.Delay(500);
        if (Time < 30.1) throw new IOException("Playback clock did not advance.");
        Toggle(); var paused = Time; await Task.Delay(180);
        if (Math.Abs(Time - paused) > .03) throw new IOException("Pause did not hold the audio clock.");
        Seek(_session!.SongDuration + 1); Toggle(); await Task.Delay(250);
        if (Time < _session.SongDuration + 1.1) throw new IOException("Credits clock did not advance.");
        Toggle(); Seek(30); await Task.Delay(600);
        _log.Write("PASS UI playback, pause, seeking and post-song credits clock.");
        if (_frameWork is not null) await _frameWork;
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream(); encoder.Save(buffer);
        var path = Path.Combine(_config.Output, "ui-" + DateTime.Now.ToString("HHmmss") + ".png");
        StandArtifacts.Png(path, buffer.ToArray(), _session!.Provenance with { Scenario = "lyric-video/stand-ui-smoke" });
        _log.Write("UI smoke saved: " + path); Close();
    }
    private static string Format(double time) => TimeSpan.FromSeconds(time).ToString(@"mm\:ss");
}
