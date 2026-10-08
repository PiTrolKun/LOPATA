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
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicExamplesTests
{
    [TestMethod]
    public async Task PackagedAudioIsExactAndRestoresItsActualTags()
    {
        Assert.HasCount(3, MusicExamples.All);
        foreach (var item in MusicExamples.All) await MusicExamples.VerifyAsync(item, default);
        var local = MusicExamples.All.Single(e => !e.Cloud && e.Variation == MusicComponentCatalog.ModelId);
        var metadata = await MusicExamples.ReadAsync(local, default);
        var restored = metadata.Restore();
        Assert.AreEqual("doom metal", metadata.Tags["genre"]);
        StringAssert.Contains(metadata.Lyrics, "ЛАПАТА");
        Assert.AreEqual("Тесты", restored.Title); Assert.AreEqual("ЛОПАТА", restored.Artist);
        Assert.AreEqual("Тест проекта", restored.Comment); Assert.AreEqual(360, restored.DurationSeconds);
        Assert.AreEqual(1026197756d, restored.Expert.Get("lm_seed")); Assert.AreEqual(763242864d, restored.Expert.Get("seed"));
        Assert.AreEqual(.56598, restored.Expert.Get("abc_sampling.temperature")); Assert.AreEqual(1.2, restored.Expert.Get("cfg_scale"));
        Assert.AreEqual(-.8402002882270573, restored.Expert.Tuning!.X);
        Assert.AreEqual(-.047468942837686844, restored.Expert.Tuning.Y);
        Assert.AreEqual(MusicAudioFormat.Mp3, restored.Output.Format); Assert.AreEqual(320, restored.Output.Bitrate);
        CollectionAssert.AreEqual(new[] { "doom metal" }, restored.Wishes.Selections["genres"]);
        Assert.AreEqual("", restored.OutputFolder); Assert.AreEqual(1, restored.Variants);
        Assert.IsTrue(metadata.Tags.ContainsKey("LOPATA_PROJECT")); StringAssert.Contains(metadata.Technical, "sample_rate");
        var cloud = MusicExamples.All.Single(e => e.Cloud);
        StringAssert.Contains(cloud.Request, "ЛОПАТА"); Assert.AreEqual("doom metal", cloud.Genre);
        foreach (var key in new[] { "LOPATA_PARAMETERS", "LOPATA_WISHES", "LOPATA_OUTPUT", "LOPATA_MODEL_REVISION" }) {
            var invalid = new Dictionary<string, string>(metadata.Tags); invalid.Remove(key);
            Assert.Throws<InvalidDataException>(() => new MusicExampleMetadata(invalid, "{}").Restore());
        }
        var damaged = new Dictionary<string, string>(metadata.Tags) { ["LOPATA_PARAMETERS"] = metadata.Tags["LOPATA_PARAMETERS"].Replace("steps=32", "steps=9999") };
        Assert.Throws<InvalidDataException>(() => new MusicExampleMetadata(damaged, "{}").Restore());
        using var files = new Files(); var saved = Path.Combine(files.Root, "example.mp3");
        MusicTrackFiles.Copy(local.Path, saved);
        CollectionAssert.AreEqual(File.ReadAllBytes(local.Path), File.ReadAllBytes(saved));
    }

    [TestMethod, DataRow(MusicComponentCatalog.ModelId), DataRow(MusicModelVariants.Bf16)]
    public async Task ExampleTransferKeepsProjectHistoryAndOutputFolder(string variation)
    {
        var restored = (await MusicExamples.ReadAsync(MusicExamples.All.Single(e => !e.Cloud && e.Variation == variation), default)).Restore();
        await ScenarioNavigationTests.Sta(() => {
            using var files = new Files(); var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
            using var view = new MusicWorkspaceControl(projects, new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
            var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T); view.Tracks.SetOutputFolder(files.Root);
            var id = view.Projects.Current.Id; view.Projects.ApplyExample(restored);
            Assert.AreEqual(restored.Lyrics, view.Editor.Lyrics); Assert.AreEqual(files.Root, view.Tracks.OutputFolder);
            Assert.AreEqual(id, view.Projects.Current.Id); Assert.HasCount(0, view.Projects.Current.Steps); Assert.IsFalse(view.Projects.Current.Persistent);
            Assert.AreEqual(restored.Expert.Get("seed"), view.Generation.ExpertSettings.Get("seed"));
            Assert.AreEqual(variation, view.Generation.Variation);
            Assert.AreEqual(restored.Expert.Tuning!.X, view.Generation.ExpertSettings.Tuning!.X);
            Assert.AreEqual(360, view.Generation.Options.DurationSeconds); Assert.AreEqual(MusicAudioFormat.Mp3, view.Generation.Options.Output.Format);
            view.Projects.SetBusy(true); Assert.Throws<InvalidOperationException>(() => view.Projects.ApplyExample(restored));
        });
    }

    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public async Task CardsAndExternalParametersAreLocalizedAndBounded(string language, bool dark)
    {
        var allMetadata = new Dictionary<string, MusicExampleMetadata>();
        foreach (var entry in MusicExamples.All.Where(e => !e.Cloud))
            allMetadata.Add(entry.Id, await MusicExamples.ReadAsync(entry, default));
        await ScenarioNavigationTests.Sta(() => {
            var l = new LocalizationService(); l.Load(language);
            using var cards = new MusicModelSelectionControl(); cards.Localize(l.T);
            var host = new Window { Content = cards, Width = 1500, Height = 840 };
            Theme(host, dark);
            Modal(host, () => {
                Assert.HasCount(2, ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray());
                var yue = ScenarioNavigationTests.LogicalDescendants(cards).OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Music.Models.Variants.yue2");
                Capture(host, language + "-cards"); yue.SelectedIndex = 1;
                var bf16Players = ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray();
                Assert.HasCount(2, bf16Players);
                CollectionAssert.AreEquivalent(new[] { "Music.Examples.Player.yue2-bf16-punk", "Music.Examples.Player.suno-doom" },
                    bf16Players.Select(AutomationProperties.GetAutomationId).ToArray());
                Capture(host, language + "-bf16-cards");
                yue.SelectedIndex = 0; Assert.HasCount(2, ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray());
            });
            foreach (var entry in MusicExamples.All) {
                var metadata = entry.Cloud ? null : allMetadata[entry.Id];
                var window = new MusicExampleParametersWindow(entry, entry.Cloud ? null : metadata, l.T, true); Theme(window, dark);
                Modal(window, () => {
                    var elements = ScenarioNavigationTests.LogicalDescendants(window).ToArray();
                    var boxes = elements.OfType<TextBox>().ToArray();
                    Assert.IsTrue(boxes.All(b => b.IsReadOnly));
                    Assert.AreEqual(entry.Cloud ? 2 : metadata!.Tags.Count + 1, boxes.Length);
                    Assert.AreEqual(entry.Cloud ? 0 : 1, elements.OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b) == "Music.Examples.Apply"));
                    if (!entry.Cloud) CollectionAssert.AreEquivalent(metadata!.Tags.Values.ToArray(), boxes.Where(b => AutomationProperties.GetAutomationId(b) != "Music.Examples.Technical").Select(b => b.Text).ToArray());
                    window.UpdateLayout(); Capture(window, language + "-" + entry.Id);
                });
            }
            var tag = ScenarioNavigationCatalog.GetTag("music_examples"); Assert.AreNotEqual(tag.DescriptionKey, l.T(tag.DescriptionKey));
        });
    }
    [TestMethod]
    public async Task Bf16DefaultsMatchAcceptedSampleWithoutFixingItsSeed()
    {
        var example = MusicExamples.All.Single(e => !e.Cloud && e.Variation == MusicModelVariants.Bf16);
        await MusicExamples.VerifyAsync(example, default);
        var metadata = await MusicExamples.ReadAsync(example, default);
        var restored = metadata.Restore();
        Assert.AreEqual(MusicModelVariants.Bf16, restored.Variation);
        Assert.AreEqual(MusicModelVariants.Bf16Revision, restored.ModelRevision);
        Assert.AreEqual("punk", metadata.Tags["genre"]);
        Assert.AreEqual(666271731d, restored.Expert.Get("seed"));
        Assert.AreEqual("7d5f8609959a470fb9e5e9cbb0acf7c3", metadata.Tags["LOPATA_JOB"]);
        var defaults = MusicModelVariants.Defaults(MusicModelVariants.Bf16);
        defaults.Validate(); Assert.AreEqual(-1d, defaults.Get("seed"));
        var sample = restored.Expert.Snapshot(); sample.Values["seed"] = -1;
        Assert.IsTrue(defaults.SameAs(sample));
        Assert.AreEqual(restored.Expert.Tuning!.X, defaults.Tuning!.X);
        Assert.AreEqual(restored.Expert.Tuning.Y, defaults.Tuning.Y);
        Assert.IsTrue(MusicModelVariants.Defaults(MusicComponentCatalog.ModelId).SameAs(new MusicExpertSettings()));
        defaults.Values["cfg_scale"] = 4;
        Assert.AreEqual(1.9, MusicModelVariants.Defaults(MusicModelVariants.Bf16).Get("cfg_scale"));
        var invalid = new Dictionary<string, string>(metadata.Tags) { ["LOPATA_VARIATION"] = MusicComponentCatalog.ModelId };
        Assert.Throws<InvalidDataException>(() => new MusicExampleMetadata(invalid, "{}").Restore());
        invalid = new(metadata.Tags) { ["LOPATA_PARAMETERS"] = metadata.Tags["LOPATA_PARAMETERS"].Replace("cfg_scale=1.9", "cfg_scale=99") };
        Assert.Throws<InvalidDataException>(() => new MusicExampleMetadata(invalid, "{}").Restore());
    }

    [TestMethod]
    public Task PackagedSamplesDecodeAndPlaySilently() => ScenarioNavigationTests.Sta(() => {
        foreach (var example in MusicExamples.All) {
            using var audio = new MusicAudioPlayer { Volume = 0 };
            string? error = null; audio.Failed += key => error = key;
            audio.Open(example.Path); Pump(() => audio.IsReady || error is not null);
            Assert.IsNull(error); Assert.IsTrue(audio.IsReady);
            Assert.IsTrue(Math.Abs(audio.Duration.TotalSeconds - example.DurationSeconds) < 1);
            audio.Toggle(); Assert.IsTrue(audio.IsPlaying); audio.Seek(TimeSpan.FromSeconds(5));
            Assert.IsTrue(audio.Position.TotalSeconds >= 4.9); audio.Pause(); Assert.IsFalse(audio.IsPlaying);
            audio.Open(null); Assert.IsFalse(audio.IsReady);
        }
        static void Pump(Func<bool> done) {
            var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(20);
            var clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            clock.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; };
            clock.Start(); try { Dispatcher.PushFrame(frame); } finally { clock.Stop(); }
            Assert.IsTrue(done(), "Audio did not become ready.");
        }
    });
    private static void Theme(Window window, bool dark)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        window.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.WhiteSmoke;
        window.Resources["PanelBrush"] = dark ? Brushes.DarkSlateGray : Brushes.White;
        window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.White;
        window.Resources["UiBodyFontSize"] = 14d;
        window.Resources["TextPrimaryBrush"] = dark ? Brushes.WhiteSmoke : Brushes.Black;
        window.Resources["TextSecondaryBrush"] = dark ? Brushes.LightGray : Brushes.DimGray;
        window.Resources["InputBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
        window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.DodgerBlue;
    }
    private static void Modal(Window window, Action action)
    {
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -10000;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
            try { action(); } catch (Exception e) { failure = e; } finally { window.Close(); }
        }));
        window.ShowDialog(); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Capture(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_MUSIC_EXAMPLE_UI_EVIDENCE"); if (string.IsNullOrEmpty(folder)) return;
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Directory.CreateDirectory(folder); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual(); using (var canvas = drawing.RenderOpen()) {
            canvas.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            canvas.DrawRectangle(new VisualBrush((FrameworkElement)window.Content), null, new Rect(((FrameworkElement)window.Content).RenderSize));
        }
        image.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-examples-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
