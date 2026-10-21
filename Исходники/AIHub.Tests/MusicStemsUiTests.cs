using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicStemsUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task ModesKeepPositionAndNormalTracksRestoreTheOriginalButton(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        Directory.CreateDirectory(files.Root);
        var voice = Path.Combine(files.Root, "voice.wav"); var music = Path.Combine(files.Root, "music.wav");
        WriteWave(voice); WriteWave(music);
        var audio = new FakeAudio(); using var player = new MusicPlayerControl(audio);
        var l = new LocalizationService(); l.Load(language); player.Localize(l.T);
        var window = new Window { Content = player, Width = 430, Height = 250, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = Brushes.Gray;
        window.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        window.Resources["SecondaryButtonBackgroundBrush"] = window.Resources["PanelBrush"];
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        window.Resources["UiBodyFontSize"] = 14d;
        window.Background = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        try {
            window.Show();
            var track = new MusicTrack(voice, "Общая карточка · Shared song", TimeSpan.FromSeconds(1), DateTime.Now) { BundleFolder = files.Root, MusicPath = music };
            player.Select(track);
            var play = ScenarioNavigationTests.LogicalDescendants(player).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Audio.Play");
            Pump(() => audio.IsReady && play.IsEnabled);
            Assert.IsNotNull(play.ContextMenu); StringAssert.Contains(AutomationProperties.GetName(play), l.T("Music.Stems.Play.Together"));
            audio.Seek(TimeSpan.FromSeconds(.4)); audio.Toggle();
            SelectMode("Voice"); Pump(() => audio.IsReady && play.IsEnabled); Assert.AreEqual(TimeSpan.FromSeconds(.4), audio.Position); Assert.IsTrue(audio.IsPlaying);
            StringAssert.Contains(AutomationProperties.GetName(play), l.T("Music.Stems.Play.Voice"));
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(play).OfType<TextBlock>().Any(t => t.Text == "🎤"));
            SelectMode("Music"); Pump(() => audio.IsReady && play.IsEnabled); Assert.IsFalse(player.CanHighlightPlayback); Assert.AreEqual(TimeSpan.FromSeconds(.4), audio.Position);
            Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(play).OfType<TextBlock>().Any(t => t.Text == "🎼"));
            SelectMode("Together"); Pump(() => audio.IsReady && play.IsEnabled); Assert.AreEqual(TimeSpan.FromSeconds(.4), audio.Position);
            Capture(window, "player-" + language);
            player.Select(track with { BundleFolder = null, MusicPath = null });
            Assert.IsNull(play.ContextMenu);
            Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(play).OfType<TextBlock>().Any(t => t.Text is "🎤" or "🎼" or "🔊"));
            void SelectMode(string name) => play.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(item => AutomationProperties.GetAutomationId(item) == "Music.Audio.Stems." + name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        }
        finally { window.Close(); }
    });

    [TestMethod, DataRow(149d), DataRow(220d)]
    public Task CheckboxFitsAndNeverEntersGlobalFormatPreferences(double width) => ScenarioNavigationTests.Sta(() => {
        var output = new MusicOutputControl { Width = width }; var l = new LocalizationService(); l.Load("ru"); output.Localize(l.T);
        var window = new Window { Content = output, Width = width + 30, Height = 220, ShowInTaskbar = false };
        window.Resources["TextPrimaryBrush"] = Brushes.Black; window.Resources["SecondaryButtonBackgroundBrush"] = Brushes.WhiteSmoke;
        window.Resources["LineBrush"] = Brushes.Gray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
        try {
            window.Show(); window.UpdateLayout();
            var toggle = ScenarioNavigationTests.LogicalDescendants(output).OfType<System.Windows.Controls.CheckBox>()
                .Single(c => AutomationProperties.GetAutomationId(c) == "Music.Output.SeparateTracks");
            Assert.IsFalse(output.SeparateTracks);
            var origin = toggle.TranslatePoint(new(0, 0), output);
            Assert.IsTrue(origin.X >= 0 && origin.X + toggle.ActualWidth <= output.ActualWidth + 1);
            output.SeparateTracks = true;
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(output.Settings).Contains("SeparateTracks"));
            Capture(window, "checkbox-" + width);
        }
        finally { window.Close(); }
    });

    private static void Pump(Func<bool> ready) {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(12);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Assert.IsTrue(ready(), "Playback preparation did not settle.");
    }
    private static void Capture(Window window, string name) {
        var root = Environment.GetEnvironmentVariable("LOPATA_STEMS_UI_EVIDENCE"); if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root); window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap(Math.Max(1, (int)content.ActualWidth), Math.Max(1, (int)content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen()) {
            var bounds = new Rect(content.RenderSize); context.DrawRectangle(window.Background ?? Brushes.WhiteSmoke, null, bounds);
            context.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        }
        image.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(root, name + ".png")); encoder.Save(output);
    }
    private static void WriteWave(string path) {
        using var w = new BinaryWriter(File.Create(path)); w.Write(0x46464952u); w.Write(192036u); w.Write(0x45564157u);
        w.Write(0x20746d66u); w.Write(16u); w.Write((ushort)1); w.Write((ushort)2); w.Write(48000u); w.Write(192000u); w.Write((ushort)4); w.Write((ushort)16);
        w.Write(0x61746164u); w.Write(192000u); w.Write(new byte[192000]);
    }
    private sealed class FakeAudio : IMusicAudioPlayer {
        public bool IsReady { get; private set; } public bool IsPlaying { get; private set; }
        public TimeSpan Duration => TimeSpan.FromSeconds(1); public TimeSpan Position { get; private set; }
        public double Volume { get; set; }
        public event Action? Changed; public event Action<string>? Failed { add { } remove { } }
        public void Open(string? path) { IsReady = path is not null; IsPlaying = false; Position = TimeSpan.Zero; Changed?.Invoke(); }
        public void Toggle() { IsPlaying = !IsPlaying; Changed?.Invoke(); }
        public void Pause() { IsPlaying = false; Changed?.Invoke(); } public void Seek(TimeSpan position) => Position = position;
        public void Dispose() { }
    }
}
