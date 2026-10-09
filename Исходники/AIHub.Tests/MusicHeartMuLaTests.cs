using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicHeartMuLaTests
{
    [TestMethod]
    public void LyricsTagsAndInstrumentalRemainSeparate()
    {
        var request = new MusicYueRequest(" hard rock, energetic,hard rock ", "[Verse]\nЁж поёт!", 7, 7, 120) { Expert = MusicHeartMuLaCatalog.Defaults() };
        var payload = MusicHeartMuLaRequest.Build(request);
        Assert.AreEqual(request.Lyrics, payload["lyrics"]);
        Assert.AreEqual("hard rock,energetic", payload["tags"]);
        Assert.AreEqual(120000, payload["max_audio_length_ms"]);
        Assert.AreEqual(240000, MusicHeartMuLaRequest.Build(request with { DurationAutomatic = true })["max_audio_length_ms"]);
        Assert.IsTrue(((List<string>)payload["warnings"]!).Any(s => s.Contains("Russian")));
        var instrumental = MusicHeartMuLaRequest.Build(request with { Wishes = MusicWishSnapshot.Capture(new() { Instrumental = true }) });
        Assert.AreEqual("", instrumental["lyrics"]);
        Assert.AreEqual(request.Lyrics, instrumental["original_lyrics"]);
        StringAssert.Contains((string)instrumental["tags"]!, "instrumental,no vocals");
    }

    [TestMethod]
    public void PresetsJobsAndCommonProjectRestoreOwnSettings()
    {
        using var files = new MusicProjectTests.Files();
        var settings = MusicHeartMuLaCatalog.Defaults(); settings.Values["seed"] = 17; settings.Values["temperature"] = .9;
        var preset = ModelExpertPresets.Create("Проверка", settings, "Expert");
        var path = Path.Combine(files.Root, "preset.json"); ModelExpertPresets.Export(path, preset);
        Assert.AreEqual(8, ModelExpertPresets.Import(path).SchemaVersion);
        Assert.IsTrue(settings.SameAs(ModelExpertPresets.Import(path).Settings));
        Assert.Throws<InvalidDataException>(() => ModelExpertPresets.Validate(preset with { SchemaVersion = 7 }));
        Assert.Throws<InvalidDataException>(() => MusicModelVariants.Transfer(settings, MusicStudioRuntime.Variation));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Тест", 1, 360, "rock", "Слова", expert: settings,
            wishes: MusicWishSnapshot.Capture(new()), outputSettings: new(), durationAutomatic: true);
        var restored = jobs.Load(job.Id);
        Assert.AreEqual(240, restored.DurationSeconds);
        Assert.AreEqual(17, restored.Variants[0].SoundSeed); Assert.AreEqual(17, restored.Variants[0].LanguageSeed);
        var tags = MusicSongMetadata.Create(restored, restored.Variants[0] with { ExecutionReceipt = "{\"test\":true}" }, 0);
        Assert.AreEqual("{\"test\":true}", tags["LOPATA_HEARTMULA_REQUEST"]);
        Assert.IsFalse(tags.ContainsKey("LOPATA_LM_SEED"));
        var snapshot = new MusicExampleMetadata(tags, "").Restore();
        Assert.AreEqual(MusicHeartMuLaCatalog.ModelName, snapshot.Model); Assert.IsTrue(snapshot.Expert.SameAs(settings));
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var project = projects.AddRequest(projects.CreateDraft(), job.Id, MusicProjectSnapshot.FromJob(job) with { ModelSettings = new() {
            [settings.Variation] = settings, [MusicStudioRuntime.Variation] = MusicModelVariants.Defaults(MusicStudioRuntime.Variation) } });
        var saved = projects.Load(project.Id);
        Assert.HasCount(2, saved.Steps[0].Snapshot.ModelSettings);
        Assert.AreEqual("Слова", saved.Steps[0].Snapshot.Lyrics);
    }

    [TestMethod]
    public async Task CardsArePinnedAndVerificationDoesNotDownload()
    {
        using var files = new MusicProjectTests.Files();
        using var preparation = new MusicPreparationService(new ManagedModelLibraryStore(Path.Combine(files.Root, "library"))) { Variation = MusicHeartMuLaCatalog.Variation };
        var models = Path.Combine(files.Root, "models"); var cards = await preparation.CheckAsync(models, null, default);
        Assert.HasCount(3, cards); Assert.IsFalse(Directory.Exists(models));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(files.Root, "consent.json"));
        foreach (var id in MusicHeartMuLaCatalog.Components) Assert.IsTrue(licenses.Entries.Any(e => e.Id == id));
        foreach (var card in cards) Assert.IsTrue(card.Files.Count > 0 && card.Files.All(f => f.SizeBytes > 0 && f.Sha256.Length == 64 && new Uri(f.SourceUrl).Scheme == "https"));
        Assert.IsTrue(MusicModelSelectionCatalog.CanOpen("heartmula", "heart3b"));
        Assert.AreEqual(ScenarioNavigationCatalog.Music, ScenarioNavigationCatalog.GetTag("music_heartmula").TargetId);
    }

    [TestMethod]
    public async Task SourceTamperingAndWrongExecutionReceiptAreRejected()
    {
        using var files = new MusicProjectTests.Files(); var staging = Path.Combine(files.Root, "source"); Directory.CreateDirectory(staging);
        var artifact = new PinnedPythonArtifact("HeartMuLa", "source", "source.zip", new FileInfo(MusicHeartMuLaSource.ArchivePath).Length, MusicHeartMuLaSource.ArchiveDigest,
            new("https://github.com/HeartMuLa/heartlib"));
        await PythonWheelExtractor.ExtractAsync(artifact, MusicHeartMuLaSource.ArchivePath, staging, default, wheelLayout: false);
        await MusicHeartMuLaSource.VerifyDirectoryAsync(staging, default);
        await File.AppendAllTextAsync(Path.Combine(staging, "src", "heartlib", "pipelines", "music_generation.py"), "\n# changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => MusicHeartMuLaSource.VerifyDirectoryAsync(staging, default));
        var payload = MusicHeartMuLaRequest.Build(new("rock", "Слова", 1, 1, 30) { Expert = MusicHeartMuLaCatalog.Defaults() });
        var receipt = JsonSerializer.SerializeToElement(new { source = MusicHeartMuLaCatalog.SourceRevision, request = payload });
        MusicHeartMuLaWorker.ValidateReceipt(receipt, payload);
        payload["seed"] = 2;
        Assert.Throws<InvalidDataException>(() => MusicHeartMuLaWorker.ValidateReceipt(receipt, payload));
    }

    [TestMethod]
    public async Task CancellationRetriesUnfinishedAudioAndKeepsCompletedResult()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Heart", 1, 30, "rock", "Слова", expert: MusicHeartMuLaCatalog.Defaults());
        var worker = new Worker { Cancel = true }; var runner = new MusicGenerationRunner(jobs, worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, default));
        Assert.IsFalse(jobs.Load(job.Id).Variants[0].Completed);
        worker.Cancel = false; await runner.RunAsync(job.Id, default); await runner.RunAsync(job.Id, default);
        Assert.AreEqual(2, worker.Calls); Assert.IsNull(jobs.Load(job.Id).Variants[0].PlanFile);
        Assert.AreEqual(worker.LastRequestReceipt, jobs.Load(job.Id).Variants[0].ExecutionReceipt);
        await Assert.ThrowsAsync<IOException>(() => MusicHeartMuLaWorker.ExecuteProcessPhaseAsync(t => Task.Delay(500, t), true, default, TimeSpan.FromMilliseconds(10)));
    }

    [STATestMethod]
    public void WorkspaceAndExpertUseHeartMuLaParametersWithoutCircleOrEditorProbe()
    {
        using var files = new MusicProjectTests.Files();
        using var view = new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root, "projects")), new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T);
        view.Generation.ConfigureVariation(MusicHeartMuLaCatalog.Variation, MusicHeartMuLaCatalog.Defaults()); view.Session.ModelChanged();
        view.Editor.Lyrics = "[Verse]\nЛОПАТА поёт"; view.ConfigureGeneration(files.Root, l.T);
        var start = ScenarioNavigationTests.LogicalDescendants(view.Generation).OfType<System.Windows.Controls.Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Music.Generation.StartPause");
        Assert.IsTrue(start.IsEnabled); Assert.AreEqual(Visibility.Collapsed, view.Generation.Tuning.Visibility);
        view.Editor.Lyrics = "New words"; Assert.IsTrue(start.IsEnabled);
        var window = new ModelExpertWindow(MusicHeartMuLaCatalog.Defaults(), l.T);
        var controls = ScenarioNavigationTests.LogicalDescendants(window).OfType<FrameworkElement>().ToArray();
        foreach (var p in MusicHeartMuLaCatalog.Parameters) Assert.IsTrue(controls.Any(e => AutomationProperties.GetAutomationId(e) == "Music.Expert." + p.Key));
        window.Close();
        var bad = MusicHeartMuLaCatalog.Defaults(); bad.Values["cfg"] = 2; Assert.Throws<InvalidDataException>(bad.Validate);
    }

    private sealed class Worker : IMusicYueWorker
    {
        public int Calls; public bool Cancel; public string LastRequestReceipt => "{\"fixture\":true}";
        public event Action<string>? Log { add {} remove {} }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) => throw new AssertFailedException("HeartMuLa must not enter YuE planning.");
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        { Calls++; if (Cancel) throw new OperationCanceledException(); MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask; }
    }
}
