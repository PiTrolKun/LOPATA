using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicExpertTests
{
    [TestMethod]
    public void NativeRequestUsesBothSamplersAndRenderingSettings()
    {
        var settings = new MusicExpertSettings();
        settings.Values["cot"] = 1; settings.Values["steps"] = 48;
        settings.Values["cfg_scale"] = 2; settings.Values["peak_clip"] = 25;
        foreach (var prefix in new[] { "abc_sampling", "semantic_sampling" }) {
            settings.Values[prefix + ".temperature"] = .42; settings.Values[prefix + ".top_p"] = .8;
            settings.Values[prefix + ".top_k"] = 13; settings.Values[prefix + ".repetition_penalty"] = 1.35;
            settings.Values[prefix + ".penalty_window"] = 25; settings.Values[prefix + ".min_tokens"] = 12;
            settings.Values[prefix + ".max_tokens"] = 300;
        }
        var request = new MusicYueRequest("rock", "Lyrics", 11, 22, 60) { Expert = settings };
        request.Validate(); using var json = JsonDocument.Parse(JsonSerializer.Serialize(request.NativeRequest));
        var root = json.RootElement;
        Assert.AreEqual("melody", root.GetProperty("cot").GetString()); Assert.AreEqual(48, root.GetProperty("steps").GetInt32());
        Assert.AreEqual(2d, root.GetProperty("cfg_scale").GetDouble()); Assert.AreEqual(25, root.GetProperty("peak_clip").GetInt32());
        foreach (var prefix in new[] { "abc_sampling", "semantic_sampling" }) {
            var native = root.GetProperty(prefix);
            foreach (var name in new[] { "temperature", "top_p", "top_k", "repetition_penalty", "penalty_window", "min_tokens", "max_tokens" })
                Assert.AreEqual(settings.Get(prefix + "." + name), native.GetProperty(name).GetDouble(), prefix + "." + name);
        }
        Assert.AreEqual(600, request.OutputReserve);
        Assert.Contains("melody-only", request.Instruction);
    }

    [TestMethod]
    public void NoPlanChangesContextAndGuidanceMemoryEstimate()
    {
        var defaults = new MusicYueRequest("", "", 1, 2);
        var off = new MusicExpertSettings(); off.Values["cot"] = 2;
        var request = defaults with { Expert = off };
        Assert.AreEqual(9000, request.OutputReserve); Assert.DoesNotContain("ABC", request.Instruction);
        var weights = new MusicWeightMemory(1024, 1024, 1024, 1000);
        var baseline = MusicHardwarePolicy.Demand(weights, 100, 0, defaults, true);
        var guided = MusicHardwarePolicy.Demand(weights, 100, 0, request, true);
        Assert.AreEqual(baseline.ContextTokens, guided.ContextTokens);
        Assert.AreEqual(baseline.KvBytes * 2, guided.KvBytes);
        Assert.AreEqual(baseline.GraphReserveBytes * 2, guided.GraphReserveBytes);
    }

    [TestMethod]
    public void ValidationRejectsUnknownMissingNonfiniteAndConflictingValues()
    {
        foreach (var (key, value) in new[] { ("steps", 0d), ("steps", 2.5), ("cfg_scale", -.5), ("seed", double.NaN),
            ("abc_sampling.min_tokens", 5000d), ("unrecognised", 1d) }) {
            var settings = new MusicExpertSettings(); settings.Values[key] = value;
            Assert.ThrowsExactly<InvalidDataException>(settings.Validate, key);
        }
        var missing = new MusicExpertSettings(); missing.Values.Remove("steps");
        Assert.ThrowsExactly<InvalidDataException>(missing.Validate);
    }

    [TestMethod]
    public void PresetsRoundTripAndCurrentSettingsAreIndependent()
    {
        InTemporaryDirectory(folder => {
            var store = new ModelExpertPresets(folder); var settings = new MusicExpertSettings(); settings.Values["steps"] = 40;
            var preset = new ModelExpertPreset("Мой / блюз", settings.Snapshot());
            store.Save([preset]); store.SetCurrent(settings);
            settings.Values["steps"] = 99;
            Assert.AreEqual(40d, store.Current().Get("steps")); Assert.AreEqual(40d, store.Load()[0].Settings.Get("steps"));
            var name = ModelExpertPresets.ExportName(preset.Name, new(2026, 10, 7));
            Assert.AreEqual("LOPATA_Preset_Мой _ блюз_YuE2_2026-10-07.json", name);
            var path = Path.Combine(folder, name); ModelExpertPresets.Export(path, preset);
            var imported = ModelExpertPresets.Import(path); Assert.AreEqual(preset.Name, imported.Name);
            Assert.IsTrue(imported.Settings.SameAs(preset.Settings));
            Assert.ThrowsExactly<InvalidDataException>(() => store.Save([preset, preset with { Name = preset.Name.ToUpperInvariant() }]));
            Assert.AreEqual(1, store.Load().Count); Assert.IsFalse(Directory.EnumerateFiles(folder, "*.tmp").Any());
        });
    }

    [TestMethod]
    public void ImportRejectsOtherModelsContractsAndUnknownFields()
    {
        InTemporaryDirectory(folder => {
            var path = Path.Combine(folder, "import.json"); var preset = new ModelExpertPreset("test", new());
            foreach (var invalid in new[] { preset with { Model = "Other" }, preset with { ContractVersion = 9 },
                preset with { SchemaVersion = 9 } }) {
                File.WriteAllText(path, JsonSerializer.Serialize(invalid));
                Assert.ThrowsExactly<InvalidDataException>(() => ModelExpertPresets.Import(path));
            }
            File.WriteAllText(path, JsonSerializer.Serialize(preset).Insert(1, "\"Execute\":\"cmd.exe\","));
            Assert.ThrowsExactly<JsonException>(() => ModelExpertPresets.Import(path));
            File.WriteAllText(path, JsonSerializer.Serialize(preset).Insert(1, "\"Model\":\"YuE2\","));
            Assert.ThrowsExactly<InvalidDataException>(() => ModelExpertPresets.Import(path));
            File.WriteAllText(path, "{\"Name\":\"test\",\"Settings\":{}}");
            Assert.ThrowsExactly<JsonException>(() => ModelExpertPresets.Import(path));
            File.WriteAllText(path, new string('x', 1_048_577));
            Assert.ThrowsExactly<InvalidDataException>(() => ModelExpertPresets.Import(path));
        });
    }

    [TestMethod]
    public void JobSnapshotsAndRepeatPreserveValuesWithoutChangingOldJobs()
    {
        InTemporaryDirectory(folder => {
            var jobs = new MusicGenerationJobs(Path.Combine(folder, "jobs")); var settings = new MusicExpertSettings();
            settings.Values["steps"] = 44; settings.Values["lm_seed"] = int.MaxValue; settings.Values["seed"] = 12;
            var job = jobs.Create(folder, folder, "song", 2, 60, "rock", "lyrics", expert: settings);
            settings.Values["steps"] = 90; var saved = jobs.Load(job.Id);
            Assert.AreEqual(44d, saved.Expert.Get("steps")); Assert.AreEqual(int.MaxValue, saved.Variants[0].LanguageSeed);
            Assert.AreEqual(0, saved.Variants[1].LanguageSeed); Assert.AreEqual(13, saved.Variants[1].SoundSeed);
            var repeat = jobs.Create(folder, folder, "repeat", 1, 60, "rock", "lyrics", saved.Variants[0], saved.Expert);
            Assert.AreEqual(saved.Variants[0].SoundSeed, repeat.Variants[0].SoundSeed); Assert.IsTrue(repeat.Expert.SameAs(saved.Expert));
            var oldJson = JsonSerializer.SerializeToNode(saved)!; oldJson.AsObject().Remove("Expert");
            File.WriteAllText(Path.Combine(jobs.Folder(job.Id), "job.json"), oldJson.ToJsonString());
            Assert.IsTrue(jobs.Load(job.Id).Expert.SameAs(new()));
        });
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ExpertWindowDescribesEveryParameterAndCancelDoesNotPersist(string language) => CheckWindow(language, false);

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ApplyPersistsTheActualEditedValue(string language) => CheckWindow(language, true);

    private static Task CheckWindow(string language, bool apply) => ScenarioNavigationTests.Sta(() => {
        InTemporaryDirectory(folder => {
            var store = new ModelExpertPresets(folder); var l = new LocalizationService(); l.Load(language);
            var settings = new MusicExpertSettings(); var window = new ModelExpertWindow(settings, l.T, store) { Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            window.Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(16, 24, 39));
            window.Resources["TextPrimaryBrush"] = Brushes.White;
            window.Resources["SecondaryButtonBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(24, 35, 54));
            window.Resources["LineBrush"] = Brushes.SlateGray; window.Resources["AccentBrush"] = Brushes.RoyalBlue;
            window.Resources["UiBodyFontSize"] = 13d;
            Exception? failure = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                try {
                    var children = Descendants(window).ToArray();
                    foreach (var p in MusicExpertCatalog.Parameters)
                        Assert.IsTrue(children.OfType<FrameworkElement>().Any(c => AutomationProperties.GetAutomationId(c) == "Music.Expert." + p.Key), p.Key);
                    Assert.IsFalse(children.OfType<TextBlock>().Any(t => t.Text.StartsWith("Music.Expert.", StringComparison.Ordinal)));
                    var steps = children.OfType<TextBox>().Single(t => AutomationProperties.GetAutomationId(t) == "Music.Expert.steps");
                    steps.Text = "48";
                    var evidence = Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE");
                    if (!string.IsNullOrWhiteSpace(evidence)) {
                        Directory.CreateDirectory(evidence); window.UpdateLayout();
                        var content = (FrameworkElement)window.Content;
                        var visual = new DrawingVisual(); using (var draw = visual.RenderOpen()) {
                            var bounds = new Rect(content.RenderSize); draw.DrawRectangle(window.Background, null, bounds);
                            draw.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
                        }
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(evidence, "expert-" + language + ".png")); encoder.Save(file);
                    }
                    children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Expert." + (apply ? "Apply" : "Cancel"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                } catch (Exception e) { failure = e; window.Close(); }
            }));
            window.ShowDialog(); if (failure is not null) throw failure;
            Assert.AreEqual(apply ? 48d : 32d, store.Current().Get("steps")); Assert.AreEqual(32d, settings.Get("steps"));
            Assert.AreEqual(apply ? 48d : 32d, window.Result.Get("steps"));
        });
    });

    [TestMethod]
    public async Task NoPlanJobUsesSnapshotAndRecoversWithoutRecomputing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LOPATA_music_expert_" + Guid.NewGuid().ToString("N"));
        try {
            var settings = new MusicExpertSettings(); settings.Values["cot"] = 2; settings.Values["steps"] = 44;
            var jobs = new MusicGenerationJobs(Path.Combine(folder, "jobs"));
            var job = jobs.Create(folder, folder, "test", 1, 30, "rock", "lyrics", expert: settings);
            var worker = new NoPlanWorker(); var runner = new MusicGenerationRunner(jobs, worker);
            await runner.RunAsync(job.Id, default);
            var saved = jobs.Load(job.Id); Assert.IsNull(saved.Variants[0].PlanFile); Assert.IsTrue(saved.Variants[0].Completed);
            await runner.RunAsync(job.Id, default); Assert.AreEqual(1, worker.Calls); Assert.AreEqual(1, jobs.Tracks(job.Id).Count);
        } finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class NoPlanWorker : IMusicYueWorker
    {
        public event Action<string>? Log;
        public int Calls;
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token)
            => throw new AssertFailedException("No-plan job attempted planning.");
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        {
            Assert.AreEqual("off", request.EffectiveExpert.Cot); Assert.AreEqual(44, request.EffectiveExpert.Integer("steps"));
            Assert.AreEqual("", request.Abc); Calls++; Log?.Invoke("test");
            using var writer = new BinaryWriter(File.Create(outputPath));
            writer.Write(0x46464952u); writer.Write(192036u); writer.Write(0x45564157u);
            writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write(48000u); writer.Write(192000u); writer.Write((ushort)4); writer.Write((ushort)16);
            writer.Write(0x61746164u); writer.Write(192000u); writer.Write(new byte[192000]);
            return Task.CompletedTask;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void InTemporaryDirectory(Action<string> action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "LOPATA_music_expert_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try { action(folder); } finally { Directory.Delete(folder, true); }
    }
}
