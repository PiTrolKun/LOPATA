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
    public async Task HeartMuLaOpusKeepsStreamCommentsSettingsAndHasNoCloudComparison()
    {
        var example = MusicExamples.ForVariation(MusicHeartMuLaCatalog.Variation, false).Single();
        Assert.AreEqual(".opus", Path.GetExtension(example.File));
        Assert.HasCount(0, MusicExamples.ForVariation(MusicHeartMuLaCatalog.Variation, true));
        var metadata = await MusicExamples.ReadAsync(example, default);
        var snapshot = metadata.Restore();
        Assert.AreEqual(MusicHeartMuLaCatalog.Variation, snapshot.Variation);
        Assert.AreEqual(.75, snapshot.Expert.Get("temperature"));
        Assert.AreEqual(492140412d, snapshot.Expert.Get("seed"));
        Assert.AreEqual(metadata.Lyrics, snapshot.Lyrics);
        Assert.IsTrue(snapshot.Lyrics.Length > 0);
        await ScenarioNavigationTests.Sta(() => {
            var l = new LocalizationService(); l.Load("ru");
            using var cards = new MusicModelSelectionControl(); cards.Localize(l.T);
            var elements = ScenarioNavigationTests.LogicalDescendants(cards).OfType<FrameworkElement>().ToArray();
            Assert.IsFalse(elements.Any(e => AutomationProperties.GetAutomationId(e) == "Music.Models.CloudExample.heartmula"));
            Assert.AreEqual(1, elements.Count(e => AutomationProperties.GetAutomationId(e) == "Music.Examples.Player." + example.Id));
            var host = new Window { Content = cards, Width = 1500, Height = 840 };
            Theme(host, true);
            Modal(host, () => {
                var title = elements.OfType<Button>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Models.Open.heartmula");
                host.UpdateLayout();
                var scroll = (ScrollViewer)cards.Content;
                var position = title.TransformToAncestor((Visual)scroll.Content).Transform(new Point());
                scroll.ScrollToVerticalOffset(Math.Max(0, position.Y - 20));
                Capture(host, "ru-heartmula-card");
            });
        });
    }

    [TestMethod]
    public async Task PackagedAudioIsExactAndRestoresItsActualTags()
    {
        Assert.HasCount(13, MusicExamples.All);
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
        var cloud = MusicExamples.All.Single(e => e.Id == "suno-doom");
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
            Assert.AreEqual(MusicModelVariants.WorkspaceVariation(variation), view.Generation.Variation);
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
                var players = ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray();
                Assert.HasCount(10, players);
                CollectionAssert.AreEquivalent(new[] { "Music.Examples.Player.yue2-studio-opera", "Music.Examples.Player.yue2-studio-hard-rock",
                    "Music.Examples.Player.suno-opera", "Music.Examples.Player.suno-hard-rock",
                    "Music.Examples.Player.ace-xl-jpop", "Music.Examples.Player.suno-jpop",
                    "Music.Examples.Player.diffrhythm2-zh", "Music.Examples.Player.diffrhythm2-en",
                    "Music.Examples.Player.diffrhythm2-ru", "Music.Examples.Player.heartmula-blues-jazz" }, players.Select(AutomationProperties.GetAutomationId).ToArray());
                Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(cards).OfType<FrameworkElement>()
                    .Any(e => AutomationProperties.GetAutomationId(e) == "Music.Models.CloudExample.diffrhythm"));
                foreach (var entry in MusicExamples.ForVariation(MusicDiffRhythmCatalog.Variation, false)) {
                    var player = players.Single(p => AutomationProperties.GetAutomationId(p) == "Music.Examples.Player." + entry.Id);
                    CollectionAssert.Contains(ScenarioNavigationTests.LogicalDescendants(player).OfType<TextBlock>()
                        .Select(t => t.Text).ToArray(), entry.DisplayTitle(l.T));
                    Assert.AreNotEqual(entry.TitleKey, entry.DisplayTitle(l.T));
                }
                var opened = new List<(string Model, string Variant)>();
                cards.OpenRequested += (model, variant) => { opened.Add((model, variant)); return Task.CompletedTask; };
                var titles = ScenarioNavigationTests.LogicalDescendants(cards).OfType<Button>()
                    .Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Music.Models.Open.", StringComparison.Ordinal)).ToArray();
                Assert.HasCount(4, titles);
                Assert.IsTrue(titles.All(b => b.IsEnabled));
                Assert.IsFalse(ScenarioNavigationTests.LogicalDescendants(cards).OfType<ComboBox>()
                    .Any(c => AutomationProperties.GetAutomationId(c).StartsWith("Music.Models.Variants.", StringComparison.Ordinal)));
                foreach (var (model, variant) in new[] { ("yue2", "studio-q8"), ("ace-step", "xl-turbo"), ("diffrhythm", "diff2"), ("heartmula", "heart3b") }) {
                    var elements = ScenarioNavigationTests.LogicalDescendants(cards).ToArray();
                    Assert.IsFalse(elements.OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "Music.Models.Variants." + model));
                    var title = elements.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Models.Open." + model);
                    var candidate = MusicModelSelectionCatalog.All.Single(m => m.Id == model);
                    Assert.HasCount(1, candidate.Variants);
                    Assert.AreEqual(candidate.Name + " · " + l.T("Music.Models.Variant." + candidate.Variants[0].LabelKey), title.Content);
                    title.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual((model, variant), opened.Last());
                }
                Assert.HasCount(4, opened);
                Assert.IsFalse(MusicModelSelectionCatalog.All.Any(c => c.Id is "regrind" or "mothersuperior" or "yue2-lora"));
                Capture(host, language + "-cards");
                cards.Localize(l.T);
                Assert.HasCount(10, ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray());
                var ace = ScenarioNavigationTests.LogicalDescendants(cards).OfType<Button>()
                    .Single(c => AutomationProperties.GetAutomationId(c) == "Music.Models.Open.ace-step");
                host.UpdateLayout();
                var scroll = (ScrollViewer)cards.Content;
                var position = ace.TransformToAncestor((Visual)scroll.Content).Transform(new Point());
                scroll.ScrollToVerticalOffset(Math.Max(0, position.Y - 20));
                Capture(host, language + "-ace-cards");
                var diff = ScenarioNavigationTests.LogicalDescendants(cards).OfType<Button>()
                    .Single(c => AutomationProperties.GetAutomationId(c) == "Music.Models.Open.diffrhythm");
                host.UpdateLayout();
                position = diff.TransformToAncestor((Visual)scroll.Content).Transform(new Point());
                scroll.ScrollToVerticalOffset(Math.Max(0, position.Y - 20));
                Capture(host, language + "-diffrhythm-cards");
                host.UpdateLayout();
                var currentPlayers = ScenarioNavigationTests.LogicalDescendants(cards).OfType<MusicExamplePlayerControl>().ToArray();
                var russian = currentPlayers.Single(p => AutomationProperties.GetAutomationId(p) == "Music.Examples.Player.diffrhythm2-ru");
                var english = currentPlayers.Single(p => AutomationProperties.GetAutomationId(p) == "Music.Examples.Player.diffrhythm2-en");
                var chinese = currentPlayers.Single(p => AutomationProperties.GetAutomationId(p) == "Music.Examples.Player.diffrhythm2-zh");
                var ruPosition = russian.TransformToAncestor(host).Transform(new Point());
                var enPosition = english.TransformToAncestor(host).Transform(new Point());
                var zhPosition = chinese.TransformToAncestor(host).Transform(new Point());
                Assert.IsTrue(ruPosition.X < enPosition.X, "Russian example must be inside the description card.");
                Assert.AreEqual(enPosition.X, zhPosition.X, .1);
                Assert.IsTrue(enPosition.Y > zhPosition.Y);
                Assert.IsTrue(russian.ActualWidth > english.ActualWidth);
                FrameworkElement ancestor = diff;
                while (ancestor is not Grid { ColumnDefinitions.Count: 2 }) ancestor = (FrameworkElement)ancestor.Parent;
                var diffRow = (Grid)ancestor;
                Assert.IsTrue(diffRow.ActualHeight < 650, "Card must fit the two-example layout plus the 50-pixel removal footer without the former empty height.");
                var labels = ScenarioNavigationTests.LogicalDescendants(cards).OfType<TextBlock>().Select(t => t.Text).ToArray();
                CollectionAssert.Contains(labels, l.T("Music.Models.diffrhythm.Memory"));
                Assert.AreNotEqual("Music.Models.diffrhythm.Memory", l.T("Music.Models.diffrhythm.Memory"));
                foreach (var key in new[] { "Description", "Pros", "Cons", "Memory" })
                    CollectionAssert.Contains(labels, l.T("Music.Models.ace-step.xl-turbo." + key));
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
    public async Task DiffExamplesRestoreThreeLanguagesWithoutCloudComparisons()
    {
        var examples = MusicExamples.ForVariation(MusicDiffRhythmCatalog.Variation, false);
        Assert.HasCount(3, examples);
        Assert.HasCount(0, MusicExamples.ForVariation(MusicDiffRhythmCatalog.Variation, true));
        CollectionAssert.AreEqual(new[] { "Китайский", "Английский", "Русский" }, examples.Select(e => e.Title).ToArray());
        foreach (var (id, word, seed) in new[] { ("diffrhythm2-zh", "深夜", 1701830214d),
            ("diffrhythm2-en", "LOPATA", 354945099d), ("diffrhythm2-ru", "ЛОПАТА", 765688443d) }) {
            var example = examples.Single(e => e.Id == id);
            var metadata = await MusicExamples.ReadAsync(example, default);
            var restored = metadata.Restore();
            StringAssert.Contains(restored.Lyrics, word);
            Assert.AreEqual(metadata.Lyrics, restored.Lyrics);
            Assert.AreEqual(MusicDiffRhythmCatalog.Variation, restored.Variation);
            Assert.AreEqual(seed, restored.Expert.Get("seed"));
            Assert.AreEqual(16d, restored.Expert.Get("steps"));
            Assert.AreEqual(1.3, restored.Expert.Get("cfg"));
            Assert.AreEqual(1d, restored.Expert.Get("experimental_ru"));
            await ScenarioNavigationTests.Sta(() => {
                using var files = new Files();
                using var view = new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root, "projects")),
                    new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
                view.Tracks.SetOutputFolder(files.Root); var project = view.Projects.Current.Id;
                view.Projects.ApplyExample(restored);
                Assert.AreEqual(project, view.Projects.Current.Id);
                Assert.AreEqual(files.Root, view.Tracks.OutputFolder);
                Assert.AreEqual(restored.Lyrics, view.Editor.Lyrics);
                Assert.IsTrue(restored.Expert.SameAs(view.Generation.ExpertSettings));
            });
        }
    }
    [TestMethod]
    public async Task AceExampleRestoresNumbersTextAndAutomaticDurationWithoutChangingProject()
    {
        var local = MusicExamples.ForVariation(MusicAceCatalog.Variation, false).Single();
        var cloud = MusicExamples.ForVariation(MusicAceCatalog.Variation, true).Single();
        var metadata = await MusicExamples.ReadAsync(local, default);
        var restored = metadata.Restore();
        Assert.AreEqual("ace-xl-jpop", local.Id); Assert.AreEqual("suno-jpop", cloud.Id);
        Assert.AreEqual(local.Pair, cloud.Pair); Assert.AreEqual("j-pop", cloud.Genre);
        Assert.AreEqual(metadata.Lyrics.Replace("\r\n", "\n"), cloud.Request.Replace("\r\n", "\n"));
        StringAssert.Contains(restored.Lyrics, "ЛАПАТА");
        Assert.AreEqual(MusicAceCatalog.Variation, restored.Variation);
        Assert.AreEqual(MusicAceCatalog.ModelName, restored.Model);
        Assert.IsNull(restored.DurationSeconds);
        Assert.AreEqual(1108693710d, restored.Expert.Get("seed"));
        Assert.AreEqual(8d, restored.Expert.Get("inference_steps"));
        Assert.AreEqual(1d, restored.Expert.Get("dcw_enabled"));
        Assert.AreEqual("low", restored.Expert.TextValues["dcw_mode"]);
        Assert.AreEqual("auto", restored.Expert.TextValues["vocal_language"]);
        Assert.AreEqual(MusicAceTuningProfile.Id, restored.Expert.Tuning!.Profile);
        var actual = metadata.Tags["LOPATA_PARAMETERS"].Split('\n').Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
        foreach (var pair in restored.Expert.Values)
            Assert.AreEqual(double.Parse(actual[pair.Key], System.Globalization.CultureInfo.InvariantCulture), pair.Value);
        foreach (var pair in restored.Expert.TextValues) Assert.AreEqual(actual[pair.Key], pair.Value);
        foreach (var replacement in new[] { "dcw_mode=low", "dcw_mode=invalid" }) {
            var damaged = new Dictionary<string, string>(metadata.Tags) { ["LOPATA_PARAMETERS"] =
                replacement.EndsWith("invalid", StringComparison.Ordinal)
                    ? metadata.Tags["LOPATA_PARAMETERS"].Replace("dcw_mode=low", replacement)
                    : metadata.Tags["LOPATA_PARAMETERS"].Replace("dcw_mode=low\n", "") };
            Assert.Throws<InvalidDataException>(() => new MusicExampleMetadata(damaged, "{}").Restore());
        }
        await ScenarioNavigationTests.Sta(() => {
            using var files = new Files();
            using var view = new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root, "projects")),
                new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
            view.Tracks.SetOutputFolder(files.Root);
            var id = view.Projects.Current.Id;
            view.Projects.ApplyExample(restored);
            Assert.AreEqual(id, view.Projects.Current.Id); Assert.HasCount(0, view.Projects.Current.Steps);
            Assert.AreEqual(files.Root, view.Tracks.OutputFolder); Assert.AreEqual(restored.Lyrics, view.Editor.Lyrics);
            Assert.AreEqual(MusicAceCatalog.Variation, view.Generation.Variation);
            Assert.IsTrue(restored.Expert.SameAs(view.Generation.ExpertSettings));
            Assert.IsNull(view.Generation.Options.DurationSeconds);
        });
    }
    [TestMethod]
    public async Task StudioPairsRestoreActualLyricsAndSettingsAndMatchCloudGenre()
    {
        var local = MusicExamples.ForVariation(MusicStudioRuntime.Variation, false);
        var cloud = MusicExamples.ForVariation(MusicStudioRuntime.Variation, true);
        CollectionAssert.AreEqual(new[] { "opera", "hard rock" }, local.Select(e => e.Genre).ToArray());
        for (var i = 0; i < local.Count; i++) {
            var metadata = await MusicExamples.ReadAsync(local[i], default);
            var restored = metadata.Restore();
            Assert.AreEqual(MusicStudioRuntime.Variation, restored.Variation);
            Assert.AreEqual(local[i].Genre, metadata.Tags["genre"]);
            Assert.AreEqual(local[i].Pair, cloud[i].Pair); Assert.AreEqual(local[i].Genre, cloud[i].Genre);
            Assert.AreEqual(metadata.Lyrics, restored.Lyrics);
            Assert.AreEqual(metadata.Lyrics.Replace("\r\n", "\n"), cloud[i].Request.Replace("\r\n", "\n"));
            StringAssert.Contains(metadata.Tags["LOPATA_STUDIO_REQUEST"], "lyrics");
            Assert.AreEqual(double.Parse(metadata.Tags["LOPATA_SEED"], System.Globalization.CultureInfo.InvariantCulture), restored.Expert.Get("seed"));
            await ScenarioNavigationTests.Sta(() => {
                using var files = new Files();
                using var view = new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root, "projects")),
                    new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
                view.Tracks.SetOutputFolder(files.Root); view.Projects.ApplyExample(restored);
                Assert.AreEqual(restored.Lyrics, view.Editor.Lyrics);
                Assert.IsTrue(restored.Expert.SameAs(view.Generation.ExpertSettings));
                Assert.AreEqual(files.Root, view.Tracks.OutputFolder);
                Assert.HasCount(0, view.Projects.Current.Steps);
            });
        }
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
