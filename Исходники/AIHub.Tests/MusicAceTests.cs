using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicAceTests
{
    [TestMethod]
    public void WishesBecomeStructuredConditionsWithoutRewritingLyrics()
    {
        var wishes = new MusicPreferences(); wishes.Select("genres", ["doom metal"]); wishes.Select("rhythm", ["waltz"]);
        wishes.Performers.Add(new("id", "Певец", "", "", "russian", [], [], ""));
        var expert = MusicAceCatalog.Defaults(); expert.Values["bpm"] = 90;
        var request = new MusicYueRequest(MusicWishPrompt.Build(wishes), "[Verse]\nЛАПАТА роет", 123, 123, 180) {
            Expert = expert, Wishes = MusicWishSnapshot.Capture(wishes) };
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        var p = data.RootElement.GetProperty("params");
        Assert.AreEqual("ru", p.GetProperty("vocal_language").GetString());
        Assert.AreEqual("3", p.GetProperty("timesignature").GetString());
        Assert.AreEqual(90, p.GetProperty("bpm").GetInt32());
        Assert.AreEqual(8, p.GetProperty("inference_steps").GetInt32());
        Assert.AreEqual(0, p.GetProperty("lm_top_k").GetInt32());
        Assert.AreEqual(request.Lyrics, p.GetProperty("lyrics").GetString());
        Assert.IsFalse(p.GetProperty("use_cot_lyrics").GetBoolean());
        Assert.AreEqual(180, p.GetProperty("duration").GetInt32());
        Assert.AreEqual("off", expert.Cot); Assert.AreEqual(0, request.OutputReserve);
    }
    [TestMethod]
    public void AmbiguousWishesDoNotChooseARandomRoleOrMeter()
    {
        var wishes = new MusicPreferences(); wishes.Select("rhythm", ["waltz", "four-four"]);
        wishes.Performers.Add(new("ru", "Русский", "", "", "russian", [], [], ""));
        wishes.Performers.Add(new("en", "English", "", "", "english", [], [], ""));
        var request = new MusicYueRequest("duet", "текст", 1, 1) { Expert = MusicAceCatalog.Defaults(), Wishes = MusicWishSnapshot.Capture(wishes), DurationAutomatic = true };
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        var p = data.RootElement.GetProperty("params");
        Assert.AreEqual("unknown", p.GetProperty("vocal_language").GetString()); Assert.AreEqual("", p.GetProperty("timesignature").GetString());
        Assert.AreEqual(-1, p.GetProperty("duration").GetInt32()); Assert.HasCount(2, data.RootElement.GetProperty("warnings").EnumerateArray().ToArray());
        request.Expert.TextValues["vocal_language"] = "ru"; request.Expert.TextValues["timesignature"] = "6";
        using var explicitData = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        Assert.AreEqual("6", explicitData.RootElement.GetProperty("params").GetProperty("timesignature").GetString());
        Assert.AreEqual("ru", explicitData.RootElement.GetProperty("params").GetProperty("vocal_language").GetString());
    }
    [TestMethod]
    public void InstrumentalAndAvoidHaveDedicatedFields()
    {
        var wishes = new MusicPreferences { Instrumental = true }; wishes.Select("avoid", ["choir"]);
        var request = new MusicYueRequest("instrumental", "ignored", 1, 1) { Expert = MusicAceCatalog.Defaults(), Wishes = MusicWishSnapshot.Capture(wishes) };
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        var p = data.RootElement.GetProperty("params");
        Assert.AreEqual("[Instrumental]", p.GetProperty("lyrics").GetString()); Assert.IsTrue(p.GetProperty("instrumental").GetBoolean());
        Assert.AreNotEqual("NO USER INPUT", p.GetProperty("lm_negative_prompt").GetString());
        request.Expert.TextValues["lm_negative_prompt"] = "noise";
        using var explicitData = JsonDocument.Parse(JsonSerializer.Serialize(MusicAceRequest.Build(request)));
        Assert.AreEqual("noise", explicitData.RootElement.GetProperty("params").GetProperty("lm_negative_prompt").GetString());
    }
    [TestMethod]
    public void ExpertPresetAndCommonProjectKeepSeparateModelSettings()
    {
        using var files = new MusicProjectTests.Files(); var expert = MusicAceCatalog.Defaults();
        expert.TextValues["keyscale"] = "F# minor"; expert.Values["inference_steps"] = 12;
        var preset = ModelExpertPresets.Create("Тест ACE", expert, "Expert");
        var path = Path.Combine(files.Root, "preset.json"); ModelExpertPresets.Export(path, preset);
        var loaded = ModelExpertPresets.Import(path); Assert.AreEqual(4, loaded.SchemaVersion); Assert.AreEqual(MusicAceCatalog.ModelName, loaded.Model);
        Assert.IsTrue(expert.SameAs(loaded.Settings));
        Assert.Throws<InvalidDataException>(() => MusicModelVariants.Transfer(expert, MusicStudioRuntime.Variation));
        var store = new MusicProjects(Path.Combine(files.Root, "projects"));
        var project = store.Fix(store.CreateDraft(), new() { Model = MusicAceCatalog.ModelName, Variation = MusicAceCatalog.Variation,
            ModelRevision = MusicAceCatalog.ModelRevision, Expert = expert, Lyrics = "Слова",
            ModelSettings = new() { [MusicAceCatalog.Variation] = expert, [MusicStudioRuntime.Variation] = MusicModelVariants.Defaults(MusicStudioRuntime.Variation) } });
        var saved = store.Load(project.Id).Saved; Assert.HasCount(2, saved.ModelSettings); Assert.AreEqual("Слова", saved.Lyrics);
        Assert.AreEqual("F# minor", saved.ModelSettings[MusicAceCatalog.Variation].TextValues["keyscale"]);
    }
    [TestMethod]
    public void DurableJobAndAudioMetadataRetainACERequest()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var expert = MusicAceCatalog.Defaults(); expert.Values["seed"] = 123; expert.TextValues["keyscale"] = "D major";
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "output"), "Песня", 1, 360, "rock", "ЛАПАТА", expert: expert,
            outputSettings: new(), durationAutomatic: true);
        var restored = jobs.Load(job.Id); Assert.IsTrue(restored.DurationAutomatic); Assert.AreEqual(123, restored.Variants[0].SoundSeed);
        Assert.AreEqual(restored.Variants[0].SoundSeed, restored.Variants[0].LanguageSeed);
        var metadata = MusicSongMetadata.Create(restored, restored.Variants[0] with { ExecutionReceipt = "{\"effective\":{}}" }, 0);
        Assert.AreEqual("ЛАПАТА", metadata["lyrics"]); Assert.AreEqual("auto", metadata["LOPATA_DURATION_REQUEST_SECONDS"]);
        StringAssert.Contains(metadata["LOPATA_PARAMETERS"], "keyscale=D major");
        Assert.IsFalse(metadata.ContainsKey("LOPATA_LM_SEED")); Assert.IsTrue(metadata.ContainsKey("LOPATA_ACE_REQUEST"));
        Assert.IsNull(MusicProjectSnapshot.FromJob(restored).DurationSeconds);
    }
    [TestMethod]
    public async Task ACEUsesOneOfficialStageAndKeepsCompletedResultOnRecovery()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "output"), "ACE", 1, 30, "rock", "Слова",
            expert: MusicAceCatalog.Defaults(), durationAutomatic: true);
        var worker = new FixtureWorker(); var runner = new MusicGenerationRunner(jobs, worker);
        await runner.RunAsync(job.Id, default); await runner.RunAsync(job.Id, default);
        Assert.AreEqual(1, worker.Calls); Assert.IsTrue(worker.Request!.DurationAutomatic);
        Assert.AreEqual("Слова", worker.Request.Lyrics);
        var completed = jobs.Load(job.Id).Variants[0];
        Assert.IsTrue(completed.Completed); Assert.IsNull(completed.PlanFile);
        Assert.AreEqual(worker.LastRequestReceipt, completed.ExecutionReceipt);
    }
    [TestMethod]
    public async Task ACECancelledAttemptDoesNotBecomeReadyAndCanBeRetried()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "output"), "ACE", 1, 30, "rock", "Слова", expert: MusicAceCatalog.Defaults());
        var worker = new FixtureWorker { Cancel = true }; var runner = new MusicGenerationRunner(jobs, worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, default));
        Assert.IsFalse(jobs.Load(job.Id).Variants[0].Completed); Assert.IsFalse(File.Exists(job.Variants[0].ResultPath));
        worker.Cancel = false; await runner.RunAsync(job.Id, default);
        Assert.IsTrue(jobs.Load(job.Id).Variants[0].Completed); Assert.AreEqual(2, worker.Calls);
    }
    private sealed class FixtureWorker : IMusicYueWorker
    {
        public int Calls; public bool Cancel; public MusicYueRequest? Request;
        public string LastRequestReceipt => "{\"fixture\":true}";
        public event Action<string>? Log { add { } remove { } }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
            throw new AssertFailedException("ACE must not enter the YuE plan stage.");
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        {
            Calls++; Request = request;
            if (Cancel) throw new OperationCanceledException();
            MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask;
        }
    }
    [TestMethod]
    public async Task ProbeDeadlineStartsAtProcessPhaseAndDoesNotLimitGeneration()
    {
        var limit = TimeSpan.FromMilliseconds(20);
        await Task.Delay(60); // Preparation may outlast the API deadline.
        await MusicAceWorker.ExecuteProcessPhaseAsync(token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, true, default, limit);
        await MusicAceWorker.ExecuteProcessPhaseAsync(token => Task.Delay(60, token), false, default, limit);
    }
    [TestMethod]
    public async Task ActualApiHangAndUserCancellationRemainDistinct()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => MusicAceWorker.ExecuteProcessPhaseAsync(
            token => Task.Delay(Timeout.Infinite, token), true, default, TimeSpan.FromMilliseconds(20)));
        StringAssert.Contains(error.Message, "Python process stage");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MusicAceWorker.ExecuteProcessPhaseAsync(
            token => Task.Delay(Timeout.Infinite, token), true, cancel.Token));
    }
    [TestMethod]
    public async Task DeliveredSourceArchivePreservesOfficialBytesAndRejectsTampering()
    {
        using var files = new MusicProjectTests.Files();
        var staging = Path.Combine(files.Root, "source"); Directory.CreateDirectory(staging);
        var artifact = new PinnedPythonArtifact("ACE", "source", "source.zip", 2_157_596,
            MusicAceSource.ArchiveDigest, new Uri("https://github.com/ace-step/ACE-Step-1.5"));
        await PythonWheelExtractor.ExtractAsync(artifact, MusicAceSource.ArchivePath, staging, default, wheelLayout: false);
        await MusicAceSource.VerifyDirectoryAsync(staging, default);
        Assert.IsTrue(Directory.Exists(Path.Combine(staging, "acestep", "models")));
        await File.AppendAllTextAsync(Path.Combine(staging, "acestep", "__init__.py"), "\n# tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => MusicAceSource.VerifyDirectoryAsync(staging, default));
    }
    [TestMethod]
    public async Task PreparationHasPinnedFilesLicensesAndNoImplicitDownload()
    {
        using var files = new MusicProjectTests.Files(); var store = new ManagedModelLibraryStore(Path.Combine(files.Root, "library"));
        using var preparation = new MusicPreparationService(store) { Variation = MusicAceCatalog.Variation };
        var root = Path.Combine(files.Root, "models"); var cards = await preparation.CheckAsync(root, null, default);
        Assert.HasCount(3, cards); Assert.IsFalse(Directory.Exists(root));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(files.Root, "consent.json"));
        foreach (var card in cards) {
            Assert.IsTrue(licenses.Entries.Any(e => e.Id == card.ModelArtifactId));
            Assert.IsTrue(card.Files.All(f => f.Sha256.Length == 64 && f.SizeBytes > 0 && new Uri(f.SourceUrl).Scheme == "https"));
        }
        Assert.HasCount(4, cards[0].Files.Where(f => f.RelativePath.EndsWith(".safetensors")).ToArray());
        Assert.IsTrue(cards[0].Files.Any(f => f.RelativePath == "acestep-v15-xl-turbo/model.safetensors.index.json"));
        Assert.IsFalse(cards[1].Files.Any(f => f.RelativePath.StartsWith("acestep-v15-turbo/")));
        Assert.IsTrue(MusicModelSelectionCatalog.CanOpen("ace-step", "xl-turbo"));
        Assert.IsFalse(MusicModelSelectionCatalog.CanOpen("ace-step", "turbo"));
    }
    [TestMethod]
    public void InvalidACEParametersCannotBecomeAnEffectivePreview()
    {
        var expert = MusicAceCatalog.Defaults(); expert.Values["bpm"] = 12; Assert.Throws<InvalidDataException>(expert.Validate);
        expert.Values["bpm"] = 0; expert.TextValues["timesteps"] = "0.2,0.5"; Assert.Throws<InvalidDataException>(expert.Validate);
        expert.TextValues["timesteps"] = "oops"; Assert.Throws<InvalidDataException>(expert.Validate);
        expert.TextValues["timesteps"] = "1,.5,0"; expert.Validate();
        expert.Values["inference_steps"] = 8.5; Assert.Throws<InvalidDataException>(expert.Validate);
        expert.Values["inference_steps"] = 8; expert.Values["cfg_scale"] = 1; Assert.Throws<InvalidDataException>(expert.Validate);
    }
    [STATestMethod]
    public void ACEWorkspaceDoesNotApplyYueCircleOrFakeContext()
    {
        var editor = new MusicLyricsEditor(); editor.Lyrics = "Слова";
        editor.ConfigureRequest("rock", "", ace: true); Assert.IsTrue(editor.CanGenerate); Assert.IsNull(editor.Usage);
        var generation = new MusicGenerationControl(); generation.SetExpertSettings(MusicAceCatalog.Defaults(), false);
        Assert.AreEqual(Visibility.Collapsed, generation.Tuning.Visibility);
        generation.ConfigureVariation(MusicStudioRuntime.Variation, MusicModelVariants.Defaults(MusicStudioRuntime.Variation));
        Assert.AreEqual(Visibility.Visible, generation.Tuning.Visibility);
        editor.ConfigureRequest("rock", MusicTextBudget.DefaultInstruction); Assert.IsFalse(editor.CanGenerate); editor.Dispose();
        var expert = new ModelExpertWindow(MusicAceCatalog.Defaults(), key => key);
        Assert.IsNotNull(expert.Content); expert.Close();
    }
}
