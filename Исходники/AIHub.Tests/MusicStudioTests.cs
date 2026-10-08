using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicStudioTests
{
    [TestMethod]
    public async Task StudioReusesExistingWeightsAndPinsItsOwnCompanionAndRuntime()
    {
        var original = MusicComponentCatalog.CreateCards("H:\\Models");
        var studio = MusicStudioRuntime.Cards("H:\\Models");
        Assert.HasCount(3, studio);
        foreach (var card in original) {
            var shared = studio.Single(c => c.ModelArtifactId == card.ModelArtifactId);
            Assert.AreEqual(card.InstallDirectory, shared.InstallDirectory);
            Assert.AreEqual(card.Files.Single().Sha256, shared.Files.Single().Sha256);
        }
        var companion = studio.Single(c => c.ModelArtifactId == MusicStudioRuntime.CompanionId);
        Assert.AreEqual(140_560_592L, companion.Files.Single().SizeBytes);
        Assert.AreEqual(MusicStudioRuntime.CompanionRevision, companion.Revision);
        Assert.AreEqual(Path.Combine(companion.InstallDirectory, "nar_lora_joint_v9.safetensors"),
            MusicModelVariants.Artifact("H:\\Models", MusicStudioRuntime.Variation, MusicStudioRuntime.CompanionId));
        Assert.IsTrue(MusicModelSelectionCatalog.CanOpen("yue2", "studio-q8"));
        await MusicStudioRuntime.VerifyAsync(default);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(Path.GetTempPath(), "studio-test-consent.json"));
        foreach (var id in MusicModelVariants.Components(MusicStudioRuntime.Variation))
            Assert.IsTrue(licenses.Entries.Any(e => e.Id == id), id);
    }

    [TestMethod]
    public void RequestPreservesTextSamplingTwoSeedsAndTheSavedPlan()
    {
        var settings = MusicModelVariants.Defaults(MusicStudioRuntime.Variation);
        settings.Values["cfg_scale"] = 1.7; settings.Values["steps"] = 48;
        settings.Values["peak_clip"] = 17;
        var request = new MusicYueRequest("doom metal", "[Verse]\nЛАПАТА — произношение", 123, 456, 30) {
            Expert = settings, Abc = "X:1\nK:C\nCDEF" };
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(request, false)));
        var root = body.RootElement;
        Assert.AreEqual(request.Lyrics, root.GetProperty("lyrics").GetString());
        Assert.AreEqual(request.Style, root.GetProperty("style").GetString());
        Assert.AreEqual(request.Abc, root.GetProperty("abc").GetString());
        Assert.AreEqual(123, root.GetProperty("lm_seed").GetInt32());
        Assert.AreEqual(456, root.GetProperty("seed").GetInt32());
        Assert.AreEqual(30, root.GetProperty("duration_seconds").GetInt32());
        Assert.AreEqual(48, root.GetProperty("steps").GetInt32());
        Assert.AreEqual(17, root.GetProperty("peak_clip").GetInt32());
        Assert.AreEqual(1.7, root.GetProperty("cfg_scale").GetDouble());
        Assert.AreEqual("wav16", root.GetProperty("output_format").GetString());
        Assert.AreEqual(750, root.GetProperty("semantic_sampling").GetProperty("max_tokens").GetInt32());
        using var plan = JsonDocument.Parse(JsonSerializer.Serialize(MusicStudioWorker.Request(request, true)));
        Assert.IsFalse(plan.RootElement.TryGetProperty("abc", out _));
        Assert.AreEqual(settings.PlanLimit, plan.RootElement.GetProperty("abc_sampling").GetProperty("max_tokens").GetInt32());
    }

    [TestMethod]
    public void JobsMetadataAndPresetsRetainTheStudioIdentity()
    {
        using var files = new MusicProjectTests.Files();
        var settings = MusicTuningProfile.Move(MusicModelVariants.Defaults(MusicStudioRuntime.Variation), -.5, .3);
        settings.Values["lm_seed"] = 123; settings.Values["seed"] = 456;
        var jobs = new MusicGenerationJobs(files.Root);
        var job = jobs.Create(files.Root, files.Root, "Studio", 1, 30, "doom metal", "Текст", expert: settings);
        var saved = jobs.Load(job.Id);
        Assert.AreEqual(MusicStudioRuntime.Revision, saved.RuntimeRevision);
        Assert.AreEqual(MusicComponentCatalog.Revision, saved.ModelRevision);
        Assert.Throws<InvalidDataException>(() => jobs.Save(saved with { RuntimeRevision = MusicYueRuntime.Revision }));
        var tags = MusicSongMetadata.Create(saved, saved.Variants[0], 0);
        Assert.AreEqual(MusicStudioRuntime.Variation, tags["LOPATA_VARIATION"]);
        Assert.AreEqual(MusicStudioRuntime.EngineRevision, tags["LOPATA_ENGINE_REVISION"]);
        Assert.Contains(MusicStudioRuntime.CompanionRevision, tags["LOPATA_ADAPTER"]);
        Assert.AreEqual("Текст", tags["lyrics"]);
        Assert.IsFalse(tags.ContainsKey("LOPATA_GGML_REVISION"));
        var preset = ModelExpertPresets.Create("Studio", settings, "Expert");
        Assert.AreEqual(3, preset.SchemaVersion);
        var library = new ModelExpertPresets(Path.Combine(files.Root, "studio"), "Expert", MusicStudioRuntime.Variation);
        library.Save([preset]);
        Assert.Throws<InvalidDataException>(() => new ModelExpertPresets(Path.Combine(files.Root, "q8")).Save([preset]));
        var path = Path.Combine(files.Root, ModelExpertPresets.ExportName(preset.Name, DateTime.Today, settings.Variation));
        ModelExpertPresets.Export(path, preset);
        Assert.AreEqual(MusicStudioRuntime.Variation, ModelExpertPresets.Import(path).Settings.Variation);
        Assert.Contains("Studio_Q8", path);
    }

    [TestMethod]
    public Task SharedProjectKeepsThreeIndependentSettingsBanks() => ScenarioNavigationTests.Sta(() => {
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        using var view = new MusicWorkspaceControl(projects, new MusicGenerationJobs(Path.Combine(files.Root, "jobs")),
            new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.ConfigureOutput(files.Root, _ => { }); view.Editor.Lyrics = "Один общий текст"; view.Projects.Fix();
        var project = view.Projects.Current.Id;
        foreach (var (variation, seed) in new[] { (MusicComponentCatalog.ModelId, 11), (MusicStudioRuntime.Variation, 22), (MusicModelVariants.Bf16, 33) }) {
            view.Projects.SwitchModel(variation);
            var settings = view.Generation.ExpertSettings; settings.Values["seed"] = seed;
            view.Generation.SetExpertSettings(settings, false);
        }
        view.Projects.SwitchModel(MusicStudioRuntime.Variation);
        Assert.AreEqual(22d, view.Generation.ExpertSettings.Get("seed"));
        view.Projects.SwitchModel(MusicComponentCatalog.ModelId);
        Assert.AreEqual(11d, view.Generation.ExpertSettings.Get("seed"));
        Assert.AreEqual(project, view.Projects.Current.Id);
        Assert.AreEqual("Один общий текст", view.Editor.Lyrics);
        Assert.HasCount(3, projects.Load(project).Saved.ModelSettings);
    });

    [TestMethod, DataRow(MusicComponentCatalog.ModelId), DataRow(MusicModelVariants.Bf16)]
    public Task RetiredVariantsOpenInStudioWithoutRewritingSubmittedHistory(string variation) => ScenarioNavigationTests.Sta(() => {
        Assert.IsFalse(MusicModelSelectionCatalog.CanOpen("yue2", "q8"));
        Assert.IsFalse(MusicModelSelectionCatalog.CanOpen("yue2", "bf16"));
        var variants = MusicModelSelectionCatalog.All.Single(m => m.Id == "yue2").Variants;
        CollectionAssert.AreEqual(new[] { "studio-q8" }, variants.Select(v => v.Id).ToArray());
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var settings = MusicTuningProfile.Move(MusicModelVariants.Defaults(variation), -.5, .3);
        settings.Values["seed"] = 123;
        var job = jobs.Create(files.Root, files.Root, "Legacy", 1, 30, "rock", "Старый текст", expert: settings);
        var snapshot = MusicProjectSnapshot.FromJob(job);
        var project = projects.AddRequest(projects.CreateDraft(), job.Id, snapshot);
        project = projects.SetOutcome(project.Id, job.Id, MusicProjectOutcome.Failed, "Recorded failure");
        var path = Path.Combine(files.Root, "projects", project.Id + ".project.json");
        var original = File.ReadAllBytes(path);
        using var view = new MusicWorkspaceControl(projects, jobs, new MusicOutputPreferences(Path.Combine(files.Root, "output.json")));
        view.Projects.Open(project.Id);
        Assert.AreEqual(MusicStudioRuntime.Variation, view.Generation.Variation);
        Assert.AreEqual(snapshot.Lyrics, view.Editor.Lyrics);
        foreach (var pair in settings.Values) Assert.AreEqual(pair.Value, view.Generation.ExpertSettings.Get(pair.Key));
        Assert.AreEqual(settings.Tuning!.X, view.Generation.ExpertSettings.Tuning!.X);
        Assert.AreEqual(MusicTuningProfile.StudioId, view.Generation.ExpertSettings.Tuning.Profile);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        Assert.AreEqual(variation, projects.Load(project.Id).Steps[0].Snapshot.Variation);
        view.Projects.Fix();
        var saved = projects.Load(project.Id);
        Assert.AreEqual(MusicStudioRuntime.Variation, saved.Saved.Variation);
        Assert.AreEqual(variation, saved.Steps[0].Snapshot.Variation);
        Assert.AreEqual(variation, jobs.Load(job.Id).Variation);
    });

    [TestMethod]
    public async Task RealStudioPlanAndAudioAndCancellationReleaseOwnedProcesses()
    {
        var root = Environment.GetEnvironmentVariable("LOPATA_STUDIO_TEST_MODELS");
        var output = Environment.GetEnvironmentVariable("LOPATA_STUDIO_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(output))
            Assert.Inconclusive("Explicitly opt in with installed models and a fresh technical output directory.");
        if (Directory.Exists(output)) throw new IOException("Technical output directory must be new.");
        Directory.CreateDirectory(output!);
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try {
            // Current user authorization covers this download and these bounded technical tests only.
            using var preparation = new MusicPreparationService { Variation = MusicStudioRuntime.Variation };
            var cards = await preparation.PrepareAsync(root!, true, null, deadline.Token);
            Assert.IsTrue(cards.All(c => c.Status == AIHub.Models.ManagedModelStatuses.Installed));
            var model = MusicModelVariants.Artifact(root!, MusicStudioRuntime.Variation, MusicComponentCatalog.ModelId);
            var decoder = MusicModelVariants.Artifact(root!, MusicStudioRuntime.Variation, MusicComponentCatalog.DecoderId);
            var settings = MusicModelVariants.Defaults(MusicStudioRuntime.Variation);
            settings.Values["abc_sampling.max_tokens"] = 128; settings.Values["steps"] = 8;
            var request = new MusicYueRequest("rock", "[Verse]\nНаш тест звучит,\nЛАПАТА говорит.", 12345, 45678, 8) { Expert = settings };
            var worker = new MusicStudioWorker();
            worker.Log += line => File.AppendAllText(Path.Combine(output!, "technical.log"), line + "\n");
            var plan = Path.Combine(output!, "score.abc");
            await worker.PlanAsync(model, request, Path.Combine(output!, "plan-request.json"), plan, deadline.Token);
            request = request with { Abc = File.ReadAllText(plan) };
            var audio = Path.Combine(output!, "audio.wav");
            await worker.SynthesizeAsync(model, decoder, request, Path.Combine(output!, "synth-request.json"), audio, deadline.Token);
            Assert.IsTrue(MusicWaveFile.ReadDuration(audio) > TimeSpan.Zero);
            Assert.IsNotNull(worker.LastRequestReceipt);
            Assert.Contains("actual=", worker.LastHardware!);
            var job = new MusicGenerationJobs(Path.Combine(output!, "jobs")).Create(root!, output!, "Studio technical",
                1, request.DurationSeconds, request.Style, request.Lyrics, expert: settings, outputSettings: new());
            var variant = job.Variants[0] with { LanguageSeed = request.LanguageSeed, SoundSeed = request.SoundSeed,
                Hardware = worker.LastHardware, UsedRuntimePack = worker.LastRuntimePack, ExecutionReceipt = worker.LastRequestReceipt };
            var metadata = MusicSongMetadata.Create(job, variant, 0);
            Assert.AreEqual(worker.LastRequestReceipt, metadata["LOPATA_STUDIO_REQUEST"]);
            await MusicAudioEncoder.Default.EncodeAsync(audio, variant.ResultPath, MusicAudioFormat.Opus, 320, metadata, deadline.Token);
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "Music.Studio"));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            worker.Log += line => { if (line.Contains("engine ready", StringComparison.Ordinal)) cancelled.Cancel(); };
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker.SynthesizeAsync(model, decoder, request,
                Path.Combine(output!, "cancel-request.json"), Path.Combine(output!, "cancel.wav"), cancelled.Token));
            Assert.IsFalse(File.Exists(Path.Combine(output!, "cancel.wav")));
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "Music.Studio"));
            var engines = Process.GetProcessesByName("yue-server");
            try {
                var state = engines.Select(p => new { p.Id, p.HasExited }).ToArray();
                File.WriteAllText(Path.Combine(output!, "process-exit.json"), JsonSerializer.Serialize(state));
                Assert.IsFalse(state.Any(p => !p.HasExited), "No running native engine may survive cancellation.");
            } finally { foreach (var engine in engines) engine.Dispose(); }
        } finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}
