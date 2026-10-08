using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicOutputUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task IdentityPlaceholdersAreNotDataAndDuplicateHasOnlyThreeAlternatives(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        var testRoot = MusicOutputTests.Temporary();
        var l = new LocalizationService(); l.Load(language); var control = new MusicGenerationControl(new MusicOutputPreferences(Path.Combine(testRoot, "output.json"))); control.Localize(l.T);
        var host = new Window { Content = control, Width = 580, Height = 700, Left = -10000, Top = -10000, ShowInTaskbar = false };
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        host.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black; host.Resources["TextSecondaryBrush"] = Brushes.Gray;
        host.Resources["WindowBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        host.Resources["PanelBrush"] = dark ? new SolidColorBrush(Color.FromRgb(23, 32, 50)) : Brushes.White;
        host.Resources["SecondaryButtonBackgroundBrush"] = dark ? new SolidColorBrush(Color.FromRgb(16, 24, 39)) : Brushes.WhiteSmoke;
        host.Resources["LineBrush"] = Brushes.SlateGray; host.Resources["AccentBrush"] = Brushes.RoyalBlue; host.Resources["UiBodyFontSize"] = 16d;
        host.Background = (Brush)host.Resources["PanelBrush"];
        Exception? failure = null;
        host.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            try {
                Assert.AreEqual("", control.Options.Artist); Assert.AreEqual("", control.Options.Title); Assert.AreEqual("", control.Options.Comment);
                var identity = Find<TextBox>("Music.Generation.Artist"); var title = Find<TextBox>("Music.Generation.Title");
                Assert.AreEqual(identity.ActualWidth, title.ActualWidth, 1);
                var comment = Find<TextBox>("Music.Generation.Comment"); Assert.AreEqual(0d, comment.BorderThickness.Left);
                comment.Focus(); Assert.AreEqual(1d, comment.BorderThickness.Left);
                comment.Text = "Заметка"; identity.Text = "Автор"; title.Text = "Песня";
                Assert.AreEqual("Заметка", control.Options.Comment);
                var format = Find<ComboBox>("Music.Output.Format"); format.SelectedItem = MusicAudioFormat.Opus;
                Assert.AreEqual(320, control.Options.Output.Bitrate);
                var extra = Find<ComboBox>("Music.Output.AdditionalFormat");
                var duplicate = Find<CheckBox>("Music.Output.Duplicate");
                Assert.AreEqual("+", ((TextBlock)duplicate.Template.FindName("Symbol", duplicate)).Text);
                Assert.AreEqual(l.T("Music.Output.Duplicate"), duplicate.ToolTip);
                duplicate.IsChecked = true;
                Assert.AreEqual("−", ((TextBlock)duplicate.Template.FindName("Symbol", duplicate)).Text);
                Assert.AreEqual(l.T("Music.Output.RemoveDuplicate"), AutomationProperties.GetName(duplicate));
                Assert.AreEqual(l.T("Music.Output.RemoveDuplicate"), duplicate.ToolTip);
                Assert.AreEqual(3, extra.Items.Count); Assert.IsFalse(extra.Items.Contains(MusicAudioFormat.Opus));
                Assert.IsNotNull(control.Options.Output.AdditionalFormat);
                control.UpdateState(true, false, false, true); host.UpdateLayout(); Capture("duplicate");
                format.SelectedItem = MusicAudioFormat.Flac; Assert.IsFalse(extra.Items.Contains(MusicAudioFormat.Flac));
                Assert.AreEqual(Visibility.Collapsed, Find<ComboBox>("Music.Output.Bitrate").Visibility);
                duplicate.IsChecked = false; Assert.IsNull(control.Options.Output.AdditionalFormat);
                Assert.AreEqual("+", ((TextBlock)duplicate.Template.FindName("Symbol", duplicate)).Text);
                Assert.AreEqual(l.T("Music.Output.Duplicate"), AutomationProperties.GetName(duplicate));
                Assert.IsFalse(extra.IsVisible);
                var saved = new MusicOutputPreferences(Path.Combine(testRoot, "output.json")).Load();
                Assert.IsNull(saved.AdditionalFormat);
                control.UpdateState(true, false, false, true); host.UpdateLayout();
                foreach (var box in ScenarioNavigationTests.LogicalDescendants(control).OfType<ComboBox>().Where(b => b.IsVisible)) {
                    var point = box.TranslatePoint(new Point(), control); Assert.IsTrue(point.X + box.ActualWidth <= control.ActualWidth + 1);
                }
                Capture("identity");
            }
            catch (Exception error) { failure = error; }
            finally { host.Close(); }
            void Capture(string state) {
                var output = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE");
                if (!string.IsNullOrEmpty(output)) {
                    Directory.CreateDirectory(output); var bitmap = new RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(control);
                    var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
                        var bounds = new Rect(control.RenderSize); canvas.DrawRectangle(host.Background, null, bounds); canvas.DrawRectangle(new VisualBrush(control), null, bounds);
                    }
                    bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var image = File.Create(Path.Combine(output, "music-output-" + language + "-" + state + ".png")); encoder.Save(image);
                }
            }
            T Find<T>(string id) where T : DependencyObject => ScenarioNavigationTests.LogicalDescendants(control).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);
        }));
        try { host.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        finally { Directory.Delete(testRoot, true); }
    });
    [TestMethod]
    public Task OpusPlaybackDecodesSeeksAndRetainsTheSource() => ScenarioNavigationTests.Sta(() => {
        var root = MusicOutputTests.Temporary();
        try {
            var wave = Path.Combine(root, "source.wav"); MusicOutputTests.WriteWave(wave);
            var opus = Path.Combine(root, "song.opus"); MusicAudioEncoder.Default.EncodeAsync(wave, opus, MusicAudioFormat.Opus, 160,
                new Dictionary<string, string>(), CancellationToken.None).GetAwaiter().GetResult();
            using var player = new MusicAudioPlayer { Volume = 0 }; string? error = null; player.Failed += message => error = message;
            player.Open(opus); Pump(() => player.IsReady || error is not null); Assert.IsNull(error); Assert.IsTrue(player.IsReady);
            Assert.IsFalse(player.IsPlaying); Assert.AreEqual(1, player.Duration.TotalSeconds, .1);
            player.Seek(TimeSpan.FromSeconds(.5)); Assert.AreEqual(.5, player.Position.TotalSeconds, .2);
            player.Open(opus); player.Open(wave); Pump(() => player.IsReady); Assert.IsFalse(player.IsPlaying);
            player.Dispose(); Assert.IsTrue(File.Exists(opus)); Assert.IsTrue(File.Exists(wave));
        }
        finally { Directory.Delete(root, true); }
    });
    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.IsTrue(done(), "Audio playback did not become ready.");
    }
}
