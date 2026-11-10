using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderSpeechPlayerWindow : Window
{
    private readonly MediaPlayer _media = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Slider _position = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new(9, 0, 6, 0) };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 6, 0) };
    private readonly Button _play;
    private readonly Func<string, string> _l;
    private readonly ReaderSpeechResult _audio;
    private readonly string _document;
    private bool _playing, _opened, _updating, _dragging, _disposed;
    private AIHub.Services.VoiceNotifications.VoicePlaybackHub.SpeechLease? _notificationSpeech;
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    internal ReaderSpeechPlayerWindow(Window owner, Func<string, string> localize, ReaderSpeechResult audio, string document)
    {
        Owner = owner; Icon = owner.Icon; _l = localize; _audio = audio; _document = document;
        Title = T("Player"); Width = 510; Height = 146; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(owner.Resources); Foreground = owner.Foreground;
        AutomationProperties.SetAutomationId(this, "Office.Reader.Speech.Player");
        var frame = new Border { Background = owner.Background, BorderBrush = (Brush)owner.Resources["ReaderLine"],
            BorderThickness = new(1), CornerRadius = new(12), Padding = new(12, 8, 12, 8) }; Content = frame;
        var grid = new Grid(); frame.Child = grid;
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var model = new TextBlock { Text = VideoNarratorCatalog.Get(audio.Voice.Id).Name + " · " + audio.Voice.Options.Speaker,
            TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Padding = new(3, 3, 8, 6), Cursor = System.Windows.Input.Cursors.SizeAll };
        AutomationProperties.SetAutomationId(model, "Office.Reader.Speech.Player.Model"); Grid.SetColumnSpan(model, 2); grid.Children.Add(model);
        model.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var close = Make("×", "ClosePlayer", () => Close()); close.Padding = new(6, 0, 6, 0); Grid.SetColumn(close, 2); grid.Children.Add(close);
        _play = Make("▶", "Play", Toggle); _play.Width = _play.Height = 48; _play.FontSize = 20;
        var template = new ControlTemplate(typeof(Button));
        var round = new FrameworkElementFactory(typeof(Border)); round.SetValue(Border.CornerRadiusProperty, new CornerRadius(24));
        round.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(37, 99, 235)));
        var caption = new FrameworkElementFactory(typeof(ContentPresenter)); caption.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        caption.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); round.AppendChild(caption); template.VisualTree = round;
        _play.Template = template; _play.Foreground = Brushes.White;
        Grid.SetRow(_play, 1); grid.Children.Add(_play);
        var track = new Grid(); track.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); track.ColumnDefinitions.Add(new());
        track.Children.Add(_time); Grid.SetColumn(_position, 1); track.Children.Add(_position);
        _position.Maximum = Math.Max(0.01, audio.Seconds); AutomationProperties.SetAutomationId(_position, "Office.Reader.Speech.Player.Seek");
        AutomationProperties.SetName(_position, T("Seek"));
        Grid.SetRow(track, 1); Grid.SetColumn(track, 1); Grid.SetColumnSpan(track, 2); grid.Children.Add(track);
        var save = Make("⇩", "Save", () => PendingSave = SaveAsync()); save.Padding = new(8, 1, 8, 1);
        Grid.SetRow(save, 2); Grid.SetColumn(save, 2); grid.Children.Add(save);
        Grid.SetRow(_status, 2); Grid.SetColumnSpan(_status, 2); grid.Children.Add(_status);
        _status.Text = T("Temporary"); UpdateTime();
        _position.ValueChanged += (_, _) => { if (!_updating && _opened && !_dragging) _media.Position = TimeSpan.FromSeconds(_position.Value); UpdateTime(); };
        _position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; if (_opened) _media.Position = TimeSpan.FromSeconds(_position.Value); }));
        _media.MediaOpened += (_, _) => { if (_disposed) return; _opened = true;
            if (_media.NaturalDuration.HasTimeSpan) _position.Maximum = _media.NaturalDuration.TimeSpan.TotalSeconds;
            _notificationSpeech = AIHub.Services.VoiceNotifications.VoicePlaybackHub.RegisterSpeech(_media.Pause, _media.Play);
            _playing = true; if (!_notificationSpeech.IsSuspended) _media.Play(); UpdatePlay(); };
        _media.MediaEnded += (_, _) => { _playing = false; _notificationSpeech?.SetActive(false); _media.Pause(); _position.Value = _position.Maximum; UpdatePlay(); };
        _media.MediaFailed += (_, _) => { _playing = false; _notificationSpeech?.SetActive(false); _status.Text = T("PlaybackFailed"); UpdatePlay(); };
        _timer.Tick += (_, _) => { if (_disposed || !_opened || _dragging) return;
            _updating = true; _position.Value = Math.Clamp(_media.Position.TotalSeconds, 0, _position.Maximum); _updating = false; UpdateTime(); };
        Loaded += (_, _) => { _media.Open(new Uri(audio.Path)); _timer.Start(); };
        Closed += (_, _) => { _disposed = true; _notificationSpeech?.Dispose(); _timer.Stop(); _media.Close(); };
        Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(owner.Left + owner.ActualWidth - Width - 25, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Max(SystemParameters.VirtualScreenTop, owner.Top + 90);
    }

    private string T(string key) => _l("Reader.Speech." + key);
    private Button Make(string caption, string key, Action action)
    {
        var button = new Button { Content = caption, ToolTip = T(key) };
        AutomationProperties.SetAutomationId(button, "Office.Reader.Speech.Player." + key); AutomationProperties.SetName(button, T(key));
        button.Click += (_, _) => action(); return button;
    }
    private void Toggle()
    {
        if (!_opened) return;
        if (_playing) _media.Pause();
        else { if (_position.Value >= _position.Maximum - .05) { _media.Position = TimeSpan.Zero; _position.Value = 0; } }
        _playing = !_playing; _notificationSpeech?.SetActive(_playing);
        if (_playing && _notificationSpeech?.IsSuspended != true) _media.Play();
        UpdatePlay();
    }
    private void UpdatePlay()
    { _play.Content = _playing ? "Ⅱ" : "▶"; _play.ToolTip = T(_playing ? "Pause" : "Play"); AutomationProperties.SetName(_play, (string)_play.ToolTip); }
    private void UpdateTime()
    {
        static string Format(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
        _time.Text = Format(_position.Value) + " / " + Format(_position.Maximum);
    }
    private async Task SaveAsync()
    {
        if (!PendingSave.IsCompleted) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = T("Save"), Filter = T("WavFilter") + "|*.wav", DefaultExt = ".wav",
            AddExtension = true, OverwritePrompt = true, FileName = Path.GetFileNameWithoutExtension(_document) + ".wav" };
        if (dialog.ShowDialog(this) != true) return;
        try { await ReaderSpeechExport.SaveAsync(_audio.Path, dialog.FileName, _document); if (!_disposed) _status.Text = T("Saved"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (!_disposed) _status.Text = error.Message.StartsWith("Reader.", StringComparison.Ordinal) ? _l(error.Message) : T("SaveFailed"); }
    }
}
