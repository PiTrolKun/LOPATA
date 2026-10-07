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
public sealed class MusicTuningTests
{
    [TestMethod]
    public void MovementProtectsPinsAndNeverTouchesUnrelatedParameters()
    {
        var original = new MusicExpertSettings(); original.Values["steps"] = 47; original.Values["seed"] = 321;
        original.Values["semantic_sampling.top_k"] = 81; original = MusicTuningProfile.Pin(original, "semantic_sampling.top_k");
        var moved = MusicTuningProfile.Move(original, 1, 1);
        Assert.AreEqual(81d, moved.Get("semantic_sampling.top_k"));
        Assert.AreEqual(1.2, moved.Get("semantic_sampling.temperature")); Assert.AreEqual(64d, moved.Get("abc_sampling.top_k"));
        foreach (var pair in original.Values.Where(p => !p.Key.EndsWith("temperature") && !p.Key.EndsWith("top_p") && !p.Key.EndsWith("top_k")))
            Assert.AreEqual(pair.Value, moved.Get(pair.Key), pair.Key);
        Assert.AreEqual(30d, original.Get("abc_sampling.top_k")); Assert.IsTrue(MusicTuningProfile.Position(moved).Approximate);
        var released = MusicTuningProfile.Release(moved, "semantic_sampling.top_k");
        Assert.AreEqual(200d, released.Get("semantic_sampling.top_k")); Assert.IsFalse(MusicTuningProfile.Position(released).Approximate);
    }
    [TestMethod]
    public void InverseRenderingIsStableAndDoesNotRoundExpertValues()
    {
        for (var i = -20; i <= 20; i++) {
            var x = i / 20d; var y = -x; var moved = MusicTuningProfile.Move(new(), x, y);
            var position = MusicTuningProfile.Position(moved);
            Assert.AreEqual(x, position.X, .000001); Assert.AreEqual(y, position.Y, .000001); Assert.IsFalse(position.Approximate);
            var disk = ModelBubbleControl.ToDisk(x, y); Assert.IsTrue(disk.X * disk.X + disk.Y * disk.Y <= 1.000001);
            var inverse = ModelBubbleControl.FromDisk(disk.X, disk.Y);
            Assert.AreEqual(x, inverse.X, .000001); Assert.AreEqual(y, inverse.Y, .000001);
        }
        var settings = new MusicExpertSettings(); settings.Values["semantic_sampling.temperature"] = 3.123456;
        settings.Values["semantic_sampling.top_p"] = .43789; settings.Values["semantic_sampling.top_k"] = 777;
        var json = JsonSerializer.Serialize(settings); var first = MusicTuningProfile.Position(settings);
        Assert.IsTrue(first.Approximate);
        for (var i = 0; i < 50; i++) Assert.AreEqual(first, MusicTuningProfile.Position(settings));
        Assert.AreEqual(json, JsonSerializer.Serialize(settings));
    }
    [TestMethod]
    public void NoPlanAndSuppliedPlanDisableOnlyCompositionAndGuidanceRemainsIndependent()
    {
        foreach (var supplied in new[] { false, true }) {
            var original = new MusicExpertSettings(); if (!supplied) original.Values["cot"] = 2;
            var moved = MusicTuningProfile.Move(original, -1, 1, supplied);
            foreach (var key in original.Values.Keys.Where(k => k.StartsWith("abc_sampling"))) Assert.AreEqual(original.Get(key), moved.Get(key));
            Assert.IsFalse(MusicTuningProfile.Position(moved, supplied).CompositionEnabled);
            var guided = MusicTuningProfile.Guidance(moved, 2);
            foreach (var key in moved.Values.Keys.Where(k => k != "cfg_scale")) Assert.AreEqual(moved.Get(key), guided.Get(key));
            Assert.AreEqual(-1d, MusicTuningProfile.Guidance(guided, null).Get("cfg_scale"));
        }
    }
    [TestMethod]
    public void RecipesApplyOnlyDeclaredFieldsAndRetainOriginalProvenance()
    {
        foreach (var recipe in MusicTuningRecipes.All) {
            var original = new MusicExpertSettings(); original.Values["steps"] = 48; original.Values["cot"] = 2;
            original.Values["seed"] = 5; var result = recipe.Apply(original);
            foreach (var key in original.Values.Keys) Assert.AreEqual(recipe.Values.GetValueOrDefault(key, original.Get(key)), result.Get(key));
            var copy = ModelExpertPresets.Create("Copy", result, "Simple");
            Assert.AreEqual(recipe.Id, copy.Recipe!.Id); Assert.IsTrue(copy.Recipe.Experimental);
            if (recipe.Id.StartsWith('R')) Assert.Contains("64 DPM2", copy.Recipe.Adaptation);
        }
    }
    [TestMethod]
    public void BothPresetLibrariesAndLegacyFilesRemainCompatibleAndBounded()
    {
        Temporary(folder => {
            var expert = new ModelExpertPresets(Path.Combine(folder, "expert"));
            var simple = new ModelExpertPresets(Path.Combine(folder, "simple"), "Simple");
            var legacy = new ModelExpertPreset("Old", new()); expert.Save([legacy]);
            var settings = MusicTuningProfile.Pin(MusicTuningProfile.Move(new(), .2, -.8), "steps");
            var preset = ModelExpertPresets.Create("My circle", settings, "Simple"); simple.Save([preset]);
            Assert.AreEqual("Old", expert.Load().Single().Name); Assert.AreEqual(1, simple.Load().Count);
            Assert.ThrowsExactly<InvalidDataException>(() => expert.Save([preset]));
            Assert.ThrowsExactly<InvalidDataException>(() => simple.Save([legacy]));
            var path = Path.Combine(folder, "preset.json"); ModelExpertPresets.Export(path, preset);
            var imported = ModelExpertPresets.Import(path); Assert.IsTrue(imported.Settings.SameAs(settings));
            CollectionAssert.AreEqual(settings.Tuning!.Pins, imported.Settings.Tuning!.Pins); Assert.AreEqual(.2, imported.Settings.Tuning.X);
            ModelExpertPresets.Export(path, legacy); Assert.IsNull(ModelExpertPresets.Import(path).Settings.Tuning);
            foreach (var bad in new[] { settings with { Tuning = settings.Tuning with { Version = 20 } },
                settings with { Tuning = settings.Tuning with { X = double.NaN } },
                settings with { Tuning = settings.Tuning with { Pins = ["steps", "steps"] } },
                settings with { Tuning = settings.Tuning with { Pins = ["execute"] } } })
                Assert.ThrowsExactly<InvalidDataException>(bad.Validate);
        });
    }
    [TestMethod]
    public void JobsAndRepeatsSnapshotIntentPinsWishesAndEffectiveValues()
    {
        Temporary(folder => {
            var jobs = new MusicGenerationJobs(Path.Combine(folder, "jobs"));
            var settings = MusicTuningProfile.Move(new(), -.25, .75); var wishes = new MusicPreferences(); wishes.Select("genres", ["blues"]);
            var snapshot = MusicWishSnapshot.Capture(wishes);
            var job = jobs.Create(folder, folder, "song", 1, 60, MusicWishPrompt.Build(wishes), "lyrics", expert: settings, wishes: snapshot);
            settings.Values["steps"] = 99; snapshot.Selections["genres"][0] = "metal";
            var saved = jobs.Load(job.Id); var repeat = jobs.Create(folder, folder, "repeat", 1, 60, saved.Style, saved.Lyrics,
                saved.Variants[0], saved.Expert, saved.Wishes);
            Assert.AreEqual("blues", saved.Wishes!.Selections["genres"][0]);
            Assert.AreEqual(32d, repeat.Expert.Get("steps")); Assert.AreEqual(saved.Expert.Tuning!.X, repeat.Expert.Tuning!.X);
            Assert.AreEqual(saved.Variants[0].LanguageSeed, repeat.Variants[0].LanguageSeed);
            using var native = JsonDocument.Parse(JsonSerializer.Serialize(new MusicYueRequest(saved.Style, saved.Lyrics, 1, 2) { Expert = saved.Expert }.NativeRequest));
            Assert.AreEqual(saved.Style, native.RootElement.GetProperty("style").GetString()); Assert.IsFalse(native.RootElement.TryGetProperty("Tuning", out _));
        });
    }
    [TestMethod]
    [DataRow("ru", true)]
    [DataRow("en", false)]
    public Task ExpertPreviewPinsAndCancelRestoreWithoutPersisting(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        Temporary(folder => {
            var l = new LocalizationService(); l.Load(language); var store = new ModelExpertPresets(folder);
            var original = MusicTuningProfile.Move(new(), .6, -.3); var window = new ModelExpertWindow(original, l.T, store);
            window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false; Resources(window, dark);
            MusicExpertSettings? preview = null; window.PreviewChanged += settings => preview = settings;
            Exception? failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try {
                    var input = Descendants(window).OfType<System.Windows.Controls.TextBox>().Single(e => AutomationProperties.GetAutomationId(e) == "Music.Expert.semantic_sampling.temperature");
                    input.Text = "2.123"; Assert.IsNotNull(preview); Assert.AreEqual(2.123, preview.Get("semantic_sampling.temperature"));
                    Assert.Contains("semantic_sampling.temperature", preview.Tuning!.Pins); Assert.IsTrue(MusicTuningProfile.Position(preview).Approximate);
                    Assert.IsTrue(store.Current().SameAs(new())); window.DialogResult = false;
                } catch (Exception e) { failure = e; window.Close(); }
            }));
            Assert.AreNotEqual(true, window.ShowDialog()); if (failure is not null) throw failure;
            Assert.IsTrue(window.Result.SameAs(original)); Assert.AreEqual(original.Tuning!.X, window.Result.Tuning!.X);
        });
    });
    [TestMethod]
    [DataRow("ru", true, 440, 610)]
    [DataRow("en", false, 300, 520)]
    public Task CircleRenderingAndRefreshNeverWriteBackToTheRequest(string language, bool dark, int width, int height) => ScenarioNavigationTests.Sta(() => {
        Temporary(folder => {
            var l = new LocalizationService(); l.Load(language); var control = new MusicTuningControl(new ModelExpertPresets(folder, "Simple"));
            Resources(control, dark); control.Localize(l.T); var changes = 0; control.SettingsChanged += _ => changes++;
            control.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
            var host = new Border { Child = control, Background = new SolidColorBrush(dark ? Color.FromRgb(24, 35, 54) : Colors.White), Padding = new(12) };
            var settings = MusicTuningProfile.Move(new(), .7, -.5); var before = JsonSerializer.Serialize(settings);
            for (var i = 0; i < 10; i++) control.Refresh(settings);
            host.Measure(new(width, height)); host.Arrange(new(0, 0, width, height)); host.UpdateLayout();
            Assert.AreEqual(0, changes); Assert.AreEqual(before, JsonSerializer.Serialize(settings));
            var bubble = Descendants(control).OfType<ModelBubbleControl>().Single(); Assert.IsTrue(bubble.ActualWidth > 50); Assert.IsTrue(bubble.ActualHeight > 50);
            Assert.DoesNotContain("Music.Tuning.", string.Join(" ", Descendants(control).OfType<TextBlock>().Select(t => t.Text)));
            if (Environment.GetEnvironmentVariable("LOPATA_U5_RENDER_DIR") is { Length: > 0 } output) {
                Directory.CreateDirectory(output); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, "circle-" + language + ".png")); encoder.Save(stream);
            }
        });
    });
    [TestMethod]
    [DataRow("ru", true, 550, 640)]
    [DataRow("en", false, 360, 620)]
    public Task FullGenerationPanelKeepsTheCircleUsable(string language, bool dark, int width, int height) => ScenarioNavigationTests.Sta(() => {
        var l = new LocalizationService(); l.Load(language); var control = new MusicGenerationControl(); Resources(control, dark);
        control.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        control.Localize(l.T); control.SetExpertSettings(new(), false);
        var host = new Border { Child = control, Background = new SolidColorBrush(dark ? Color.FromRgb(24, 35, 54) : Colors.White) };
        host.Measure(new(width, height)); host.Arrange(new(0, 0, width, height)); host.UpdateLayout();
        var bubble = Descendants(control).OfType<ModelBubbleControl>().Single();
        if (Environment.GetEnvironmentVariable("LOPATA_U5_RENDER_DIR") is { Length: > 0 } output) {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "panel-" + language + ".png")); encoder.Save(stream);
        }
        var minimum = width >= 440 ? 220 : 140;
        Assert.IsTrue(bubble.ActualWidth >= minimum, $"Circle size: {bubble.ActualWidth} x {bubble.ActualHeight}; tuning: {control.Tuning.ActualWidth} x {control.Tuning.ActualHeight}");
        Assert.IsTrue(bubble.ActualHeight >= minimum);
    });
    [TestMethod]
    [DataRow("ru", true)]
    [DataRow("en", false)]
    public Task RecipeWindowIsAnExplicitDraftWithFullDescriptions(string language, bool dark) => ScenarioNavigationTests.Sta(() => {
        Temporary(folder => {
            var l = new LocalizationService(); l.Load(language); var store = new ModelExpertPresets(folder, "Simple");
            var original = MusicTuningProfile.Pin(new(), "steps"); original.Values["steps"] = 55;
            foreach (var apply in new[] { false, true }) {
                var window = new MusicRecipeWindow(original, l.T, store) { Left = -10000, Top = -10000, ShowInTaskbar = false };
                Resources(window, dark); Exception? failure = null;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                    try {
                        var choices = Descendants(window).OfType<System.Windows.Controls.ListBox>().Single(); choices.SelectedIndex = 1;
                        var text = string.Join(" ", Descendants(window).OfType<TextBlock>().Select(t => t.Text));
                        Assert.Contains(l.T("Music.Tuning.Description.S1"), text); Assert.Contains("https://github.com/", text);
                        Assert.DoesNotContain("Music.Tuning.", text); Assert.IsTrue(window.Result.SameAs(original));
                        if (!apply && Environment.GetEnvironmentVariable("LOPATA_U5_RENDER_DIR") is { Length: > 0 } output) {
                            window.UpdateLayout(); var content = (FrameworkElement)window.Content;
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            var visual = new DrawingVisual();
                            using (var drawing = visual.RenderOpen()) {
                                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                                drawing.DrawRectangle((Brush)window.FindResource("WindowBackgroundBrush"), null, bounds);
                                drawing.DrawRectangle(new VisualBrush(content), null, bounds);
                            }
                            bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            Directory.CreateDirectory(output);
                            using var stream = File.Create(Path.Combine(output, "recipes-" + language + ".png")); encoder.Save(stream);
                        }
                        var action = Descendants(window).OfType<System.Windows.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == (apply ? "Music.Apply" : "Music.Cancel"));
                        action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    } catch (Exception e) { failure = e; window.Close(); }
                }));
                Assert.AreEqual(apply, window.ShowDialog() == true); if (failure is not null) throw failure;
                Assert.AreEqual(55d, window.Result.Get("steps")); Assert.IsTrue(window.Result.Tuning!.Pins.Contains("steps"));
                Assert.AreEqual(apply ? .55 : original.Get("abc_sampling.temperature"), window.Result.Get("abc_sampling.temperature"));
                Assert.AreEqual(0, store.Load().Count);
            }
        });
    });
    private static void Resources(FrameworkElement element, bool dark)
    {
        element.Resources["WindowBackgroundBrush"] = new SolidColorBrush(dark ? Colors.Black : Colors.White);
        element.Resources["TextPrimaryBrush"] = dark ? Brushes.White : Brushes.Black;
        element.Resources["SecondaryButtonBackgroundBrush"] = dark ? Brushes.DarkSlateGray : Brushes.LightGray;
        element.Resources["LineBrush"] = Brushes.SlateGray; element.Resources["AccentBrush"] = Brushes.RoyalBlue; element.Resources["UiBodyFontSize"] = 13d;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Temporary(Action<string> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-u5-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try { action(folder); } finally { Directory.Delete(folder, true); }
    }
}
