using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAceRecipeTests
{
    [TestMethod]
    public void EveryRecipeChangesOnlyDeclaredFieldsAndRejectsYuE()
    {
        var settings = MusicAceCatalog.Defaults();
        settings.Values["seed"] = 123; settings.Values["bpm"] = 120;
        settings.Values["inference_steps"] = 12; settings.Values["offload_to_cpu"] = 1;
        settings.TextValues["vocal_language"] = "ru"; settings.TextValues["keyscale"] = "C minor";
        settings.TextValues["timesteps"] = "1,.5,0";
        settings = MusicTuningProfile.Pin(MusicTuningProfile.Pin(settings, "lm_temperature"), "seed");
        var original = settings.Snapshot();
        Assert.AreEqual(7, MusicTuningRecipes.For(settings.Variation).Count);
        foreach (var recipe in MusicAceRecipes.All) {
            var result = recipe.Apply(settings);
            Assert.IsTrue(original.SameAs(settings));
            foreach (var pair in settings.Values) Assert.AreEqual(recipe.Values.GetValueOrDefault(pair.Key, pair.Value), result.Get(pair.Key), recipe.Id + ": " + pair.Key);
            foreach (var pair in settings.TextValues) Assert.AreEqual(recipe.TextValues.GetValueOrDefault(pair.Key, pair.Value), result.TextValues[pair.Key], recipe.Id + ": " + pair.Key);
            CollectionAssert.AreEquivalent(settings.Tuning!.Pins.Except(recipe.Values.Keys).ToArray(), result.Tuning!.Pins);
            Assert.IsTrue(recipe.Matches(result)); Assert.AreEqual(recipe.Id, result.Tuning.SimplePreset);
            Assert.Throws<InvalidDataException>(() => recipe.Apply(new()));
            Assert.Throws<InvalidDataException>(() => recipe.Apply(MusicModelVariants.Defaults(MusicStudioRuntime.Variation)));
            var payload = MusicAceRequest.Build(new("doom metal", "[Verse]\nЛАПАТА", 1, 1) { Expert = result });
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var p = json.RootElement.GetProperty("params");
            Assert.AreEqual("doom metal", p.GetProperty("caption").GetString());
            Assert.AreEqual("[Verse]\nЛАПАТА", p.GetProperty("lyrics").GetString());
            Assert.AreEqual("ru", p.GetProperty("vocal_language").GetString());
        }
        Assert.Throws<InvalidDataException>(() => MusicTuningRecipes.All[0].Apply(settings));
        Assert.AreEqual(7, MusicTuningRecipes.For(MusicStudioRuntime.Variation).Count);
    }
    [TestMethod]
    public void TextModesCountAsRecipeChangesAndHeunClearsTheOverridingSchedule()
    {
        var dcw = MusicAceRecipes.All.Single(r => r.Id == "ACE.DcwThink");
        var result = dcw.Apply(MusicAceCatalog.Defaults());
        result.TextValues["dcw_mode"] = "low"; Assert.IsFalse(dcw.Matches(result));
        var heun = MusicAceRecipes.All.Single(r => r.Id == "ACE.Heun");
        result.TextValues["timesteps"] = "1,.7,.3,0";
        var heunResult = heun.Apply(result);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(new("rock", "words", 1, 1) { Expert = heunResult })));
        var p = request.RootElement.GetProperty("params");
        Assert.AreEqual("heun", p.GetProperty("sampler_mode").GetString());
        Assert.AreEqual("ode", p.GetProperty("infer_method").GetString());
        Assert.AreEqual(8, p.GetProperty("inference_steps").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, p.GetProperty("timesteps").ValueKind);
    }
    [TestMethod]
    public void RecipeOriginAndExactSettingsSurviveExportLibraryAndProject()
    {
        using var files = new MusicProjectTests.Files();
        var store = new ModelExpertPresets(files.Root, "Simple", MusicAceCatalog.Variation);
        var profiles = MusicAceRecipes.All.Select(r => ModelExpertPresets.Create(r.Id, r.Apply(MusicAceCatalog.Defaults()), "Simple")).ToArray();
        store.Save(profiles); var restored = store.Load();
        for (var i = 0; i < profiles.Length; i++) {
            var path = Path.Combine(files.Root, "recipe.json"); ModelExpertPresets.Export(path, restored[i]);
            var imported = ModelExpertPresets.Import(path);
            Assert.AreEqual(6, imported.SchemaVersion); Assert.IsTrue(profiles[i].Settings.SameAs(imported.Settings));
            Assert.AreEqual(MusicAceRecipes.All[i].Source, imported.Recipe!.Source);
            CollectionAssert.AreEquivalent(MusicAceRecipes.All[i].Fields, imported.Recipe.Fields);
            Assert.Throws<InvalidDataException>(() => ModelExpertPresets.Validate(imported with { SchemaVersion = 5 }));
            Assert.Throws<InvalidDataException>(() => ModelExpertPresets.Validate(imported with { Recipe = imported.Recipe with { Fields = ["abc_sampling.temperature"] } }));
        }
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, files.Root, "Recipe", 1, 30, "rock", "words", expert: profiles[3].Settings);
        Assert.IsTrue(profiles[3].Settings.SameAs(MusicProjectSnapshot.FromJob(jobs.Load(job.Id)).Expert));
        StringAssert.Contains(MusicSongMetadata.Create(job, job.Variants[0], 0)["LOPATA_TUNING"], "ACE.DcwThink");
    }
    [TestMethod]
    public void LegacyACEProfilesRemainReadableAndTheRequestRecordsTwentySteps()
    {
        using var files = new MusicProjectTests.Files();
        var old = MusicAceCatalog.Defaults(); old.Values["inference_steps"] = 200;
        foreach (var schema in new[] { 4, 5 }) {
            var preset = ModelExpertPresets.Create("Old", old, "Expert") with { SchemaVersion = schema };
            var path = Path.Combine(files.Root, "old.json"); ModelExpertPresets.Export(path, preset);
            var imported = ModelExpertPresets.Import(path);
            Assert.AreEqual(200, imported.Settings.Get("inference_steps"));
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(new("rock", "words", 1, 1) { Expert = imported.Settings })));
            Assert.AreEqual(20, request.RootElement.GetProperty("params").GetProperty("inference_steps").GetInt32());
            Assert.AreEqual(1, request.RootElement.GetProperty("warnings").GetArrayLength());
        }
        Assert.AreEqual(20, MusicAceCatalog.Parameters.Single(p => p.Key == "inference_steps").Maximum);
        old.Values["inference_steps"] = 201; Assert.Throws<InvalidDataException>(old.Validate);
    }
    [STATestMethod]
    public void ExpertPreviewRejectsNewValuesAboveTheRuntimeLimit()
    {
        using var files = new MusicProjectTests.Files();
        var expert = new ModelExpertWindow(MusicAceCatalog.Defaults(), key => key, new(files.Root, "Expert", MusicAceCatalog.Variation)) {
            Left = -10000, Top = -10000, ShowInTaskbar = false };
        expert.Show(); expert.UpdateLayout();
        var values = new List<MusicExpertSettings>(); expert.PreviewChanged += values.Add;
        var input = Descendants(expert).OfType<System.Windows.Controls.TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Expert.inference_steps");
        input.Text = "21"; Assert.IsEmpty(values);
        input.Text = "20"; Assert.AreEqual(20, values.Single().Get("inference_steps"));
        expert.Close();
    }
    [TestMethod, DataRow("ru", true), DataRow("ru", false), DataRow("en", true), DataRow("en", false)]
    public Task RecipeWindowLocalizesEveryRecipeAndOnlyApplyChangesTheResult(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var l = new LocalizationService(); l.Load(language);
        var original = MusicAceCatalog.Defaults(); original.Values["seed"] = 123;
        var store = new ModelExpertPresets(files.Root, "Simple", MusicAceCatalog.Variation);
        foreach (var apply in new[] { false, true }) {
            var window = new MusicRecipeWindow(original, l.T, store) { Left = -10000, Top = -10000, ShowInTaskbar = false };
            window.Resources["WindowBackgroundBrush"] = dark ? Brushes.Black : Brushes.White;
            window.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
            window.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.LightGray;
            window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue; window.Resources["UiBodyFontSize"] = 13d;
            Exception? failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try {
                    var choices = Descendants(window).OfType<System.Windows.Controls.ListBox>().Single(); Assert.AreEqual(8, choices.Items.Count);
                    for (var i = 1; i < choices.Items.Count; i++) {
                        choices.SelectedIndex = i;
                        var text = string.Join(" ", Descendants(window).OfType<TextBlock>().Select(t => t.Text));
                        var recipe = MusicAceRecipes.All[i - 1];
                        Assert.Contains(l.T("Music.Tuning.Description." + recipe.Id), text);
                        Assert.Contains(l.T("Music.Tuning.Original." + recipe.Id), text);
                        Assert.Contains(recipe.Source, text); Assert.DoesNotContain("Music.Tuning.", text); Assert.DoesNotContain("Music.Ace.", text);
                        foreach (var key in recipe.Fields) Assert.Contains(l.T("Music.Ace.Parameter." + key), text);
                        Assert.IsTrue(original.SameAs(window.Result));
                    }
                    choices.SelectedIndex = 4; window.UpdateLayout();
                    if (!apply && Environment.GetEnvironmentVariable("AIHUB_ACE_RECIPES_UI_REPORT") is { Length: > 0 } output) {
                        var content = (FrameworkElement)window.Content;
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) {
                            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                            drawing.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, bounds);
                            drawing.DrawRectangle(new VisualBrush(content), null, bounds);
                        }
                        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(output);
                        using var stream = File.Create(Path.Combine(output, $"recipes-{language}-{(dark ? "dark" : "light")}.png")); encoder.Save(stream);
                    }
                    Descendants(window).OfType<System.Windows.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == (apply ? "Music.Apply" : "Music.Cancel"))
                        .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                } catch (Exception e) { failure = e; window.Close(); }
            }));
            Assert.AreEqual(apply, window.ShowDialog() == true); if (failure is not null) throw failure;
            Assert.AreEqual(123, window.Result.Get("seed")); Assert.AreEqual(apply ? "ACE.DcwThink" : null, window.Result.Tuning?.SimplePreset);
            Assert.IsTrue(original.SameAs(window.Result) == !apply); Assert.IsEmpty(store.Load());
        }
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
