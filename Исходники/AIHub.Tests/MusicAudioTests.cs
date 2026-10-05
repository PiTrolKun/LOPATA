using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAudioTests
{
    [TestMethod]
    public void PathsPreserveBothEndsAndFitAvailableWidth()
    {
        const string path = @"C:\Очень длинный путь\LOPATA\Произведения\Музыка";
        var shortened = MusicAudioUi.Shorten(path, 24, s => s.Length);
        Assert.IsTrue(shortened.Length <= 24); StringAssert.StartsWith(shortened, "C:"); StringAssert.EndsWith(shortened, "Музыка");
        StringAssert.Contains(shortened, "..."); Assert.AreEqual(path, MusicAudioUi.Shorten(path, 200, s => s.Length));
        Assert.AreEqual("", MusicAudioUi.Shorten(path, 2, s => s.Length));
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { MusicOutputFolder = path }));
        Assert.AreEqual(path, settings!.MusicOutputFolder);
    }

    [TestMethod]
    public void ExportCreatesSeparateCopyAndSamePathDoesNotDamageSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-music-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.wav"); var copy = Path.Combine(root, "result.wav");
        try
        {
            File.WriteAllBytes(source, [1, 2, 3, 4]); MusicTrackFiles.Copy(source, copy); MusicTrackFiles.Copy(source, source);
            CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(copy));
            Assert.AreEqual(2, Directory.GetFiles(root).Length);
        }
        finally { File.Delete(source); File.Delete(copy); Directory.Delete(root); }
    }

    [TestMethod]
    public Task EmptyWorkspaceHasNoTracksAndDisablesFileActionsInBothLanguagesAndThemes() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService();
        foreach (var language in new[] { "ru", "en" })
        foreach (var dark in new[] { true, false })
        {
            l.Load(language); using var workspace = new MusicWorkspaceControl(); workspace.Localize(l.T);
            workspace.ConfigureOutput(@"C:\Users\Example\Music\LOPATA\Long folder\Songs", _ => { });
            var host = new Window { Content = workspace, Width = 1200, Height = 850 }; Theme(host, dark);
            Modal(host, () =>
            {
                var list = Desc<ListBox>(workspace.Tracks).Single(); Assert.AreEqual(0, list.Items.Count);
                Assert.IsNull(workspace.Tracks.SelectedTrack);
                Assert.IsTrue(Desc<TextBlock>(workspace.Player).Any(t => t.Text == l.T("Music.Audio.NoTrack")));
                Assert.IsTrue(Desc<TextBlock>(workspace.Player).Any(t => t.Text == "00:00 / 00:00"));
                Assert.IsFalse(Desc<TextBlock>(workspace).Any(t => t.Text == l.T("Music.Audio.ExampleHint")));
                foreach (var id in new[] { "Play", "Save", "Repeat" }) Assert.IsFalse(FindButton(workspace.Player, id).IsEnabled);
                Assert.IsTrue(Desc<Button>(workspace.Tracks).Where(b => Id(b) == "Copy").All(b => !b.IsEnabled));
                Assert.AreEqual(l.T("Music.Audio.Repeat"), FindButton(workspace.Player, "Repeat").ToolTip);
                Assert.AreEqual(0, Desc<ScrollViewer>(workspace.Player).Count());
                Assert.IsTrue(list.Items.OfType<ListBoxItem>().All(item => item.Content is Border { BorderThickness.Left: 1 }));
                FindButton(workspace.Player, "Volume").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popup = Desc<System.Windows.Controls.Primitives.Popup>(workspace.Player).Single(); Assert.IsTrue(popup.IsOpen); popup.IsOpen = false;
                Snapshot(host, "music-player-" + language + "-" + dark);
            });
        }
    });

    [TestMethod]
    public Task PlayerHandlesSeekingVolumeAndReplacementWithoutAutoplay() => ScenarioNavigationTests.Sta(() =>
    {
        var audio = new FakeAudio(); using var player = new MusicPlayerControl(audio); var l = new LocalizationService(); l.Load("ru"); player.Localize(l.T);
        var file = Path.GetTempFileName();
        try
        {
            player.Select(new(file, "First.wav", TimeSpan.FromSeconds(104), DateTime.Now)); Assert.IsFalse(audio.IsPlaying);
            FindButton(player, "Play").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.IsTrue(audio.IsPlaying);
            Desc<Slider>(player).Single(s => AutomationProperties.GetAutomationId(s) == "Music.Audio.Seek").Value = 55;
            Assert.AreEqual(TimeSpan.FromSeconds(55), audio.Position);
            Desc<Slider>(player).Single(s => AutomationProperties.GetAutomationId(s) == "Music.Audio.VolumeSlider").Value = .3;
            Assert.AreEqual(.3, audio.Volume, .001);
            player.Select(new(file, "Second.wav", TimeSpan.FromSeconds(120), DateTime.Now)); Assert.IsFalse(audio.IsPlaying); Assert.AreEqual(TimeSpan.Zero, audio.Position);
            player.Select(new(null, "Music.Audio.ExampleOne", TimeSpan.FromSeconds(104), DateTime.Now)); Assert.IsFalse(FindButton(player, "Play").IsEnabled);
            Assert.AreEqual("00:00 / 01:44", Desc<TextBlock>(player).Single(t => t.Text.Contains(" / ")).Text);
        }
        finally { File.Delete(file); }
    });

    [TestMethod]
    public Task FirstRealTrackIsSelectedAndSelectionSurvivesLocalizing() => ScenarioNavigationTests.Sta(() =>
    {
        var tracks = new MusicTracksControl(); var l = new LocalizationService(); l.Load("ru"); tracks.Localize(l.T);
        var file = Path.GetTempFileName();
        try
        {
            var track = new MusicTrack(file, "Real.wav", TimeSpan.FromSeconds(104), DateTime.Now);
            tracks.AddTrack(track); l.Load("en"); tracks.Localize(l.T);
            var list = Desc<ListBox>(tracks).Single(); Assert.AreEqual(1, list.Items.Count); Assert.AreEqual(track, tracks.SelectedTrack);
            Assert.IsTrue(FindButton(tracks, "Copy").IsEnabled);
        }
        finally { File.Delete(file); }
    });

    [TestMethod]
    public Task NativePlayerReadsSilentWavSeeksAndNeverDeletesSource() => ScenarioNavigationTests.Sta(() =>
    {
        var file = Path.Combine(Path.GetTempPath(), "lopata-music-" + Guid.NewGuid() + ".wav");
        try
        {
            using (var stream = File.Create(file))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write("RIFF"u8); writer.Write(32036); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(32000); writer.Write(new byte[32000]);
            }
            using var player = new MusicAudioPlayer { Volume = 0 }; player.Open(file);
            PumpUntil(() => player.IsReady); Assert.AreEqual(2, player.Duration.TotalSeconds, .1); Assert.IsFalse(player.IsPlaying);
            player.Seek(TimeSpan.FromSeconds(1)); Assert.AreEqual(1, player.Position.TotalSeconds, .2);
            player.Open(null); Assert.IsFalse(player.IsReady); Assert.IsTrue(File.Exists(file));
            string? error = null; player.Failed += key => error = key; player.Open(file + ".missing"); Assert.AreEqual("Music.Audio.Missing", error);
            player.Dispose(); Assert.IsTrue(File.Exists(file));
        }
        finally { File.Delete(file); }
    });

    [TestMethod]
    public Task NarrowPanelsKeepControlsInsideAndFolderOnTheSameRow() => ScenarioNavigationTests.Sta(() =>
    {
        var l = new LocalizationService(); l.Load("ru"); using var player = new MusicPlayerControl(); var tracks = new MusicTracksControl();
        player.Localize(l.T); tracks.Localize(l.T);
        tracks.ConfigureFolder(@"C:\Users\Example\Music\Long folder\Songs", _ => { });
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new(190) }); grid.RowDefinitions.Add(new());
        grid.Children.Add(player); Grid.SetRow(tracks, 1); grid.Children.Add(tracks);
        var host = new Window { Content = grid, Width = 224, Height = 650 }; Theme(host, true);
        Modal(host, () =>
        {
            host.UpdateLayout();
            foreach (var control in new FrameworkElement[] { player, tracks })
            foreach (var button in Desc<Button>(control))
            {
                var origin = button.TranslatePoint(new Point(), control);
                Assert.IsTrue(origin.X >= -1 && origin.X + button.ActualWidth <= control.ActualWidth + 1);
            }
            var path = Desc<TextBlock>(tracks).Single(t => t.ToolTip is string value && value.StartsWith("C:"));
            StringAssert.Contains(path.Text, "..."); StringAssert.EndsWith(path.Text, "Songs");
            Assert.AreEqual(TextWrapping.NoWrap, path.TextWrapping);
            Snapshot(host, "music-player-narrow");
        });
    });

    private static void PumpUntil(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(ready(), "MediaPlayer did not become ready.");
    }
    private static string Id(Button button) => AutomationProperties.GetAutomationId(button).Replace("Music.Audio.", "");
    private static Button FindButton(DependencyObject root, string id) => Desc<Button>(root).Single(b => Id(b) == id);
    private static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(root).OfType<T>();
    private static void Modal(Window window, Action action)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -10000; window.ShowInTaskbar = false;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        { try { action(); } catch (Exception e) { failure = e; } finally { window.Close(); } }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Theme(FrameworkElement element, bool dark)
    {
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        element.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black; element.Resources["TextSecondaryBrush"] = Brushes.Gray;
        element.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        element.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        element.Resources["SecondaryButtonBackgroundBrush"] = element.Resources["PanelBrush"]; element.Resources["LineBrush"] = Brushes.Gray;
        element.Resources["AccentBrush"] = Brushes.RoyalBlue; element.Resources["UiBodyFontSize"] = 14d;
    }
    private static void Snapshot(Window window, string name)
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE"); if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root); window.UpdateLayout(); var visual = (FrameworkElement)window.Content; var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
        { var bounds = new Rect(visual.RenderSize); context.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, bounds); context.DrawRectangle(new VisualBrush(visual), null, bounds); }
        bitmap.Render(drawing); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, name + ".png")); encoder.Save(output);
    }
    private sealed class FakeAudio : IMusicAudioPlayer
    {
        public bool IsReady { get; private set; } public bool IsPlaying { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(120); public TimeSpan Position { get; private set; } public double Volume { get; set; } = .75;
        public event Action? Changed;
        public event Action<string>? Failed { add { } remove { } }
        public void Open(string? path) { IsReady = path is not null; IsPlaying = false; Position = TimeSpan.Zero; Changed?.Invoke(); }
        public void Toggle() { IsPlaying = !IsPlaying; Changed?.Invoke(); } public void Pause() { IsPlaying = false; Changed?.Invoke(); }
        public void Seek(TimeSpan position) => Position = position;
        public void Dispose() { }
    }
}
