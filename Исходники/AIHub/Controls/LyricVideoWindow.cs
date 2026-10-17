using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LyricVideo;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Slider = System.Windows.Controls.Slider;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class LyricVideoWindow : Window
{
    private readonly ClipConfig _config;
    private readonly ClipWork _work;
    private readonly ClipPreferences _preferences;
    private readonly Func<string, string> _l;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
    private readonly DispatcherTimer _settingsTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly MediaPlayer _player = new();
    private readonly Stopwatch _creditsClock = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, Margin = new(12, 0, 12, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    private readonly TextBlock _clock = new() { MinWidth = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play, _export, _cancel, _openResult;
    private readonly ProgressBar _progress = new() { Height = 4, Maximum = 1, Margin = new(0, 8, 0, 0) };
    private readonly LyricVideoSettings _settings;
    private ClipOptions _options = new();
    private ClipSession? _session;
    private SceneRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private Task? _loadWork, _frameWork, _exportWork;
    private CancellationTokenSource? _exportCancellation;
    private bool _ready, _playing, _credits, _closing, _allowClose, _updatingSeek, _dragging, _applying;
    private double _creditsBase, _lastFrame = -1;
    private string? _requestId, _result;
    public string SourcePath => _config.Song;
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<string>? ViewFileRequested;

    public LyricVideoWindow(string source, ClipWork work, Func<string, string> l, string language,
        ClipPreferences? preferences = null)
    {
        _config = new(source, Path.Combine(ClipWork.DirectoryPath, "preview"), language);
        _work = work; _l = l; _preferences = preferences ?? ClipPreferences.Default;
        MusicWishUi.PrepareWindow(this, l("LyricVideo.Title"), "Music.Video.Workspace", 1450);
        Title = l("LyricVideo.Title"); Width = 1450; Height = 900; MinWidth = 980; MinHeight = 670;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(this, "Music.Video.Workspace");
        var root = new DockPanel { Margin = new(18) }; Content = root;
        var header = new TextBlock { Text = l("LyricVideo.Title") + " · " + Path.GetFileName(source),
            FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var bottom = new StackPanel { Margin = new(0, 14, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var transport = new DockPanel(); bottom.Children.Add(transport);
        _play = Button(l("LyricVideo.Play"), () => Toggle(), "Play"); _play.IsEnabled = false; transport.Children.Add(_play);
        transport.Children.Add(Button(l("LyricVideo.Beginning"), () => { if (_ready) Seek(0); }, "Beginning"));
        DockPanel.SetDock(_clock, Dock.Right); transport.Children.Add(_clock); transport.Children.Add(_seek);
        var actions = new WrapPanel { Margin = new(0, 9, 0, 0) }; bottom.Children.Add(actions);
        _export = Button(l("LyricVideo.Export"), async () => await ExportAsync(), "Export"); _export.IsEnabled = false; actions.Children.Add(_export);
        _cancel = Button(l("LyricVideo.Cancel"), () => _exportCancellation?.Cancel(), "Cancel"); _cancel.IsEnabled = false; actions.Children.Add(_cancel);
        _openResult = Button(l("LyricVideo.OpenResult"), () => { if (_result is not null) ViewFileRequested?.Invoke(_result); }, "OpenResult");
        _openResult.Visibility = Visibility.Collapsed; actions.Children.Add(_openResult);
        bottom.Children.Add(_progress); bottom.Children.Add(_status);
        var content = new Grid(); content.ColumnDefinitions.Add(new()); content.ColumnDefinitions.Add(new() { Width = new GridLength(290) }); root.Children.Add(content);
        var frame = new Border { Background = Brushes.Black, Child = _image, BorderThickness = new(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); content.Children.Add(frame);
        _settings = new(l) { Margin = new(14, 0, 2, 0) };
        var scroll = new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); content.Children.Add(scroll);
        try { _options = _preferences.Load(); } catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _status.Text = l("LyricVideo.PreferencesError"); }
        _settings.Set(_options); _settings.Changed += () => { _settingsTimer.Stop(); _settingsTimer.Start(); };
        _settingsTimer.Tick += async (_, _) => { _settingsTimer.Stop(); await ApplySettingsAsync(); };
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; Seek(_seek.Value); }));
        _seek.ValueChanged += (_, _) => { if (!_updatingSeek && !_dragging && _ready) Seek(_seek.Value); };
        _timer.Tick += (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) RequestFrame(); };
        _player.MediaOpened += (_, _) => { _ready = true; _play.IsEnabled = _export.IsEnabled = true; RequestFrame(); Ready.TrySetResult(); };
        _player.MediaEnded += (_, _) => { _credits = true; _creditsBase = _session!.SongDuration; _creditsClock.Restart(); };
        _player.MediaFailed += (_, _) => { _ready = false; _status.Text = l("LyricVideo.PlaybackFailed"); Ready.TrySetResult(); };
        _work.Changed += WorkChanged;
        Loaded += async (_, _) => { _loadWork = LoadAsync(); await _loadWork; };
        Closing += OnClosing;
    }
    private static Button Button(string caption, Action handler, string id)
    {
        var button = new Button { Content = caption, Padding = new(10, 7, 10, 7), Margin = new(0, 0, 7, 0) };
        AutomationProperties.SetAutomationId(button, "Music.Video." + id); button.Click += (_, _) => handler(); return button;
    }
}
