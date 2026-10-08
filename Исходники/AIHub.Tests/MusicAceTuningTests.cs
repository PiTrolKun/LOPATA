using System.IO;
using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAceTuningTests
{
    [TestMethod]
    public void CircleChangesOnlyThreeLMFieldsAndKeepsPinsAtReset()
    {
        var settings = MusicAceCatalog.Defaults(); settings.Values["inference_steps"] = 12;
        var moved = MusicTuningProfile.Move(settings, 1, -1);
        Assert.AreEqual(1.15, moved.Get("lm_temperature")); Assert.AreEqual(.98, moved.Get("lm_top_p"));
        Assert.AreEqual(1, moved.Get("lm_cfg_scale")); Assert.AreEqual(0, moved.Get("lm_top_k"));
        foreach (var field in settings.Values.Keys.Except(MusicAceTuningProfile.Fields)) Assert.AreEqual(settings.Get(field), moved.Get(field));
        CollectionAssert.AreEquivalent(settings.TextValues.ToArray(), moved.TextValues.ToArray());
        moved.Values["lm_temperature"] = 1.6; moved = MusicTuningProfile.Pin(moved, "lm_temperature");
        var reset = MusicTuningProfile.ResetCircle(moved);
        Assert.AreEqual(1.6, reset.Get("lm_temperature")); Assert.AreEqual(.9, reset.Get("lm_top_p")); Assert.AreEqual(2, reset.Get("lm_cfg_scale"));
        Assert.AreEqual(12, reset.Get("inference_steps"));
        var released = MusicTuningProfile.Release(reset, "lm_temperature"); Assert.AreEqual(.85, released.Get("lm_temperature"));
        Assert.IsEmpty(released.Tuning!.Pins);
    }
    [TestMethod]
    public void ProjectionDoesNotRewriteOffTrajectoryExpertValues()
    {
        var settings = MusicAceCatalog.Defaults(); settings.Values["lm_temperature"] = 1.73; settings.Values["lm_top_p"] = .52;
        settings.Values["lm_cfg_scale"] = 8; var original = settings.Snapshot();
        var position = MusicTuningProfile.Position(settings);
        Assert.IsTrue(position.Approximate); Assert.AreEqual(1, position.Y); Assert.IsTrue(original.SameAs(settings)); Assert.IsNull(settings.Tuning);
        var centered = MusicTuningProfile.Position(MusicAceCatalog.Defaults());
        Assert.AreEqual(0, centered.X, .000001); Assert.AreEqual(0, centered.Y); Assert.IsFalse(centered.Approximate);
    }
    [TestMethod]
    public void CompletelyDisabledLMDoesNotChangeNumbersButCoTStillEnablesCircle()
    {
        var settings = MusicAceCatalog.Defaults();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) settings.Values[key] = 0;
        Assert.IsFalse(MusicTuningProfile.Position(settings).CompositionEnabled);
        Assert.IsTrue(settings.SameAs(MusicTuningProfile.Move(settings, 1, 1)));
        settings.Values["use_cot_caption"] = 1;
        Assert.IsTrue(MusicTuningProfile.Position(settings).CompositionEnabled);
        Assert.AreEqual(1.15, MusicTuningProfile.Move(settings, 1, 1).Get("lm_temperature"));
    }
    [TestMethod]
    public void ProfilesReceiptsAndSongMetadataPreserveExactValuesAndOldACEPresets()
    {
        using var files = new MusicProjectTests.Files();
        var settings = MusicTuningProfile.Move(MusicAceCatalog.Defaults(), -.5, .5);
        settings = MusicTuningProfile.Pin(settings, "lm_top_p");
        var preset = ModelExpertPresets.Create("ACE", settings, "Simple");
        var path = Path.Combine(files.Root, "preset.json"); ModelExpertPresets.Export(path, preset);
        var restored = ModelExpertPresets.Import(path).Settings; Assert.IsTrue(settings.SameAs(restored));
        Assert.AreEqual(MusicAceTuningProfile.Id, restored.Tuning!.Profile); Assert.AreEqual(-.5, restored.Tuning.X);
        CollectionAssert.AreEqual(settings.Tuning!.Pins, restored.Tuning.Pins);
        var legacy = ModelExpertPresets.Create("Old", MusicAceCatalog.Defaults(), "Expert") with { SchemaVersion = 4 };
        ModelExpertPresets.Validate(legacy); Assert.IsNull(legacy.Settings.Tuning);
        Assert.Throws<InvalidDataException>(() => ModelExpertPresets.Validate(preset with { SchemaVersion = 4 }));
        Assert.Throws<InvalidDataException>(() => (settings with { Tuning = new() }).Validate());
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(new("rock", "[Verse]\nЛАПАТА", 1, 1) { Expert = settings })));
        Assert.AreEqual(MusicAceTuningProfile.Id, request.RootElement.GetProperty("tuning").GetProperty("Profile").GetString());
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, files.Root, "Song", 1, 30, "rock", "[Verse]\nЛАПАТА", expert: settings);
        var metadata = MusicSongMetadata.Create(job, job.Variants[0], 0);
        StringAssert.Contains(metadata["LOPATA_TUNING"], MusicAceTuningProfile.Id);
        Assert.IsTrue(settings.SameAs(MusicProjectSnapshot.FromJob(jobs.Load(job.Id)).Expert));
    }
    [TestMethod]
    public void SoundCandidateNeverTurnsDCWOnOrChangesThePrimaryCircle()
    {
        var current = MusicTuningProfile.Move(MusicAceCatalog.Defaults(), -.5, .5);
        current.Values["dcw_enabled"] = 0; Assert.IsTrue(current.SameAs(MusicAceTuningProfile.MoveSound(current, 1, 1)));
        current.Values["dcw_enabled"] = 1; current.TextValues["dcw_mode"] = "low";
        var sound = MusicAceTuningProfile.MoveSound(current, 1, 1);
        Assert.AreEqual(.1, sound.Get("dcw_scaler")); Assert.AreEqual(.02, sound.Get("dcw_high_scaler"));
        foreach (var field in MusicAceTuningProfile.Fields) Assert.AreEqual(current.Get(field), sound.Get(field));
        Assert.AreEqual(current.Tuning!.X, sound.Tuning!.X); Assert.AreEqual("low", sound.TextValues["dcw_mode"]);
    }
    [STATestMethod]
    public void CircleAndExternalRecipesRenderACEWithoutYuEFields()
    {
        var settings = MusicAceCatalog.Defaults(); var tuning = new MusicTuningControl();
        tuning.ConfigureVariation(settings.Variation); tuning.Localize(key => key); tuning.Refresh(settings);
        var window = new MusicRecipeWindow(settings, key => key); Assert.IsNotNull(window.Content); window.Close();
        foreach (var key in new[] { "thinking", "use_cot_metas", "use_cot_caption", "use_cot_language" }) settings.Values[key] = 0;
        tuning.Refresh(settings);
        var generation = new MusicGenerationControl(); generation.SetExpertSettings(settings, false);
        Assert.IsTrue(generation.ExpertSettings.SameAs(settings));
    }
    [STATestMethod]
    public void LocalizedCircleFitsBothLayoutsAndThemes()
    {
        foreach (var language in new[] { "ru", "en" }) foreach (var dark in new[] { true, false }) {
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", language + ".json")))!;
            var tuning = new MusicTuningControl(); tuning.ConfigureVariation(MusicAceCatalog.Variation);
            tuning.Localize(key => labels[key]); tuning.Refresh(MusicAceCatalog.Defaults());
            var frame = new System.Windows.Controls.Border { Child = tuning, Padding = new(16) };
            frame.Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
            var brushes = new Dictionary<string, string> {
                ["PanelBrush"] = dark ? "#172033" : "#FFFFFF", ["TextPrimaryBrush"] = dark ? "#F8FAFC" : "#1F1F1F",
                ["TextSecondaryBrush"] = dark ? "#AAB4C4" : "#5D6470", ["LineBrush"] = dark ? "#2D374B" : "#DADDE3",
                ["SecondaryButtonBackgroundBrush"] = dark ? "#111827" : "#F8F8F8", ["AccentBrush"] = "#2563EB" };
            foreach (var pair in brushes) frame.Resources[pair.Key] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(pair.Value));
            frame.Background = (System.Windows.Media.Brush)frame.Resources["PanelBrush"]; frame.Resources["UiBodyFontSize"] = 14d;
            foreach (var width in new[] { 360, 620 }) {
                var size = new System.Windows.Size(width, 430); frame.Measure(size); frame.Arrange(new System.Windows.Rect(size)); frame.UpdateLayout();
                Assert.IsTrue(tuning.ActualWidth > 0); Assert.IsTrue(tuning.DesiredSize.Height <= 398);
                var folder = Environment.GetEnvironmentVariable("AIHUB_ACE_TUNING_UI_REPORT");
                if (string.IsNullOrEmpty(folder)) continue;
                Directory.CreateDirectory(folder);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, 430, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(frame); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(folder, $"{language}-{(dark ? "dark" : "light")}-{width}.png")); encoder.Save(file);
            }
        }
    }
}
