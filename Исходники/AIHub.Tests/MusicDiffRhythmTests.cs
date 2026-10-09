using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicDiffRhythmTests
{
    [TestMethod]
    public void RequestPreservesRussianOriginalAndSeparatesStyleAndSections()
    {
        var request = new MusicYueRequest("doom metal; duet", "[Verse 1: MALE]\nЛАПАТА роет!\n[Chorus]\nЁж поёт.", 7, 7, 120) {
            Expert = MusicDiffRhythmCatalog.Defaults() };
        var data = MusicDiffRhythmRequest.Build(request);
        Assert.AreEqual(request.Lyrics, data["original_lyrics"]);
        Assert.AreEqual("[verse]\nЛАПАТА роет!\n[chorus]\nЁж поёт.", data["lyrics"]);
        Assert.AreEqual(request.Style, data["style"]);
        Assert.AreEqual(120, data["duration"]);
        Assert.AreEqual("euler", data["solver"]);
        Assert.IsTrue(((List<string>)data["warnings"]!).Any(s => s.Contains("Experimental Russian")));
        Assert.Throws<InvalidDataException>(() => MusicDiffRhythmRequest.Build(request with { Lyrics = "[Unknown]\nСлова" }));
        Assert.Throws<InvalidDataException>(() => MusicDiffRhythmRequest.Build(request with {
            Wishes = MusicWishSnapshot.Capture(new() { Instrumental = true }) }));
        Assert.Throws<InvalidDataException>(() => MusicDiffRhythmRequest.Build(request with { DurationSeconds = 300 }));
        Assert.AreEqual(240, MusicDiffRhythmRequest.Build(request with { DurationAutomatic = true })["duration"]);
    }

    [TestMethod]
    public void PresetsAndMetadataRestoreOwnParametersAndCommonProject()
    {
        using var files = new MusicProjectTests.Files();
        var expert = MusicDiffRhythmCatalog.Defaults(); expert.Values["seed"] = 123; expert.Values["cfg"] = 1.8;
        var preset = ModelExpertPresets.Create("Русский тест", expert, "Expert");
        var path = Path.Combine(files.Root, "preset.json"); ModelExpertPresets.Export(path, preset);
        var imported = ModelExpertPresets.Import(path);
        Assert.AreEqual(7, imported.SchemaVersion); Assert.IsTrue(expert.SameAs(imported.Settings));
        Assert.Throws<InvalidDataException>(() => MusicModelVariants.Transfer(expert, MusicStudioRuntime.Variation));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Песня", 1, 120, "rock", "ЛАПАТА",
            expert: expert, wishes: MusicWishSnapshot.Capture(new()), outputSettings: new());
        var restored = jobs.Load(job.Id);
        Assert.AreEqual(123, restored.Variants[0].SoundSeed); Assert.AreEqual(123, restored.Variants[0].LanguageSeed);
        var receipt = "{\"frontend\":\"experimental-ru-espeak-v1\"}";
        var tags = MusicSongMetadata.Create(restored, restored.Variants[0] with { ExecutionReceipt = receipt }, 0);
        Assert.AreEqual(receipt, tags["LOPATA_DIFFRHYTHM_REQUEST"]); Assert.IsFalse(tags.ContainsKey("LOPATA_LM_SEED"));
        var snapshot = new MusicExampleMetadata(tags, "").Restore();
        Assert.AreEqual(MusicDiffRhythmCatalog.ModelName, snapshot.Model); Assert.AreEqual("ЛАПАТА", snapshot.Lyrics);
        Assert.IsTrue(expert.SameAs(snapshot.Expert));
        Assert.Throws<InvalidDataException>(() => jobs.Save(job with { DurationSeconds = 300 }));
        var projectStore = new MusicProjects(Path.Combine(files.Root, "projects"));
        var first = MusicProjectSnapshot.FromJob(job) with { ModelSettings = new() {
            [expert.Variation] = expert, [MusicStudioRuntime.Variation] = MusicModelVariants.Defaults(MusicStudioRuntime.Variation),
            [MusicAceCatalog.Variation] = MusicAceCatalog.Defaults() } };
        var project = projectStore.AddRequest(projectStore.CreateDraft(), job.Id, first);
        project = projectStore.SetOutcome(project.Id, job.Id, MusicProjectOutcome.Cancelled, "Отменено");
        project = projectStore.Fix(project, first with { Lyrics = "Другой текст" });
        var saved = projectStore.Load(project.Id);
        Assert.HasCount(3, saved.Saved.ModelSettings); Assert.AreEqual("ЛАПАТА", saved.Steps[0].Snapshot.Lyrics);
        Assert.AreEqual(MusicProjectOutcome.Cancelled, saved.Steps[0].Outcome);
    }

    [TestMethod]
    public async Task PreparationChecksPinnedFilesWithoutDownloadingAndShowsLicenses()
    {
        using var files = new MusicProjectTests.Files();
        using var preparation = new MusicPreparationService(new ManagedModelLibraryStore(Path.Combine(files.Root, "library"))) {
            Variation = MusicDiffRhythmCatalog.Variation };
        var root = Path.Combine(files.Root, "models");
        var cards = await preparation.CheckAsync(root, null, default);
        Assert.HasCount(3, cards); Assert.IsFalse(Directory.Exists(root));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(files.Root, "consent.json"));
        foreach (var id in MusicDiffRhythmCatalog.Components) Assert.IsTrue(licenses.Entries.Any(e => e.Id == id));
        foreach (var card in cards) Assert.IsTrue(card.Files.All(f => f.SizeBytes > 0 && f.Sha256.Length == 64 && new Uri(f.SourceUrl).Scheme == "https"));
        StringAssert.Contains(cards[1].License, "CC-BY-NC-4.0");
        Assert.IsTrue(MusicModelSelectionCatalog.CanOpen("diffrhythm", "diff2"));
        Assert.IsFalse(MusicModelSelectionCatalog.All.Any(m => m.Id is "regrind" or "mothersuperior" or "yue2-lora"));
    }

    [TestMethod]
    public async Task SourceIntegrityRejectsChangedAuthorCode()
    {
        using var files = new MusicProjectTests.Files(); var staging = Path.Combine(files.Root, "source");
        Directory.CreateDirectory(staging);
        var artifact = new PinnedPythonArtifact("DiffRhythm", "source", "source.zip", 25109972, MusicDiffRhythmSource.ArchiveDigest,
            new("https://huggingface.co/spaces/ASLP-lab/DiffRhythm2"));
        await PythonWheelExtractor.ExtractAsync(artifact, MusicDiffRhythmSource.ArchivePath, staging, default, wheelLayout: false);
        await MusicDiffRhythmSource.VerifyDirectoryAsync(staging, default);
        await File.AppendAllTextAsync(Path.Combine(staging, "diffrhythm2", "utils.py"), "\n# changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => MusicDiffRhythmSource.VerifyDirectoryAsync(staging, default));
    }

    [TestMethod]
    public async Task InterruptedAttemptRetriesOneStageAndCompletedAudioDoesNotRepeat()
    {
        using var files = new MusicProjectTests.Files(); var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "Diff", 1, 30, "rock", "ЛАПАТА", expert: MusicDiffRhythmCatalog.Defaults());
        var worker = new Worker { Cancel = true }; var runner = new MusicGenerationRunner(jobs, worker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(job.Id, default));
        Assert.IsFalse(jobs.Load(job.Id).Variants[0].Completed); Assert.IsFalse(File.Exists(job.Variants[0].ResultPath));
        worker.Cancel = false; await runner.RunAsync(job.Id, default); await runner.RunAsync(job.Id, default);
        Assert.AreEqual(2, worker.Calls); Assert.IsTrue(jobs.Load(job.Id).Variants[0].Completed);
        Assert.IsNull(jobs.Load(job.Id).Variants[0].PlanFile);
        Assert.AreEqual(worker.LastRequestReceipt, jobs.Load(job.Id).Variants[0].ExecutionReceipt);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MusicDiffRhythmWorker.ExecuteProcessPhaseAsync(t => Task.Delay(50,t),true,cancel.Token));
        await Assert.ThrowsAsync<IOException>(() => MusicDiffRhythmWorker.ExecuteProcessPhaseAsync(t => Task.Delay(500,t),true,default,TimeSpan.FromMilliseconds(10)));
    }

    [STATestMethod]
    public void ExpertAndWorkspaceDoNotBorrowYueOrAceCircle()
    {
        var generation = new MusicGenerationControl(); generation.SetExpertSettings(MusicDiffRhythmCatalog.Defaults(), false);
        Assert.AreEqual(Visibility.Collapsed, generation.Tuning.Visibility);
        var window = new ModelExpertWindow(MusicDiffRhythmCatalog.Defaults(), key => key);
        var controls = ScenarioNavigationTests.LogicalDescendants(window).OfType<FrameworkElement>().ToArray();
        foreach (var p in MusicDiffRhythmCatalog.Parameters)
            Assert.IsTrue(controls.Any(e => AutomationProperties.GetAutomationId(e) == "Music.Expert." + p.Key));
        window.Close();
        generation.ConfigureVariation(MusicStudioRuntime.Variation, MusicModelVariants.Defaults(MusicStudioRuntime.Variation));
        Assert.AreEqual(Visibility.Visible, generation.Tuning.Visibility);
        var settings = MusicDiffRhythmCatalog.Defaults(); settings.Values["solver"] = 4;
        Assert.Throws<InvalidDataException>(settings.Validate);
        Assert.AreEqual(ScenarioNavigationCatalog.Music, ScenarioNavigationCatalog.GetTag("music_diffrhythm").TargetId);
    }

    [TestMethod]
    public void BufferedNormalOutputDoesNotDisplaceFailureTrace()
    {
        var failure = MusicDiffRhythmWorker.FailureDetail(1,
            ["Traceback:", "FileNotFoundError: muq/model.safetensors"],
            Enumerable.Repeat("Loading weights from local directory", 100));
        StringAssert.Contains(failure, "FileNotFoundError: muq/model.safetensors");
        Assert.IsFalse(failure.Contains("Loading weights from local directory"));
        StringAssert.Contains(MusicDiffRhythmWorker.FailureDetail(1, [], ["Native failure"]), "Native failure");
        Assert.IsTrue(MusicDiffRhythmWorker.FailureDetail(1, [new string('x', 65536)], []).Length < 33000);
    }

    [STATestMethod]
    public void StartIsImmediateAfterLanguageChangeAndReconfigureWithoutRuntimeProbe()
    {
        using var files = new MusicProjectTests.Files();
        using var view = new MusicWorkspaceControl(new MusicProjects(Path.Combine(files.Root, "projects")),
            new MusicGenerationJobs(Path.Combine(files.Root, "jobs")), new(Path.Combine(files.Root, "output.json")));
        var l = new LocalizationService(); l.Load("ru"); view.Localize(l.T);
        view.Generation.ConfigureVariation(MusicDiffRhythmCatalog.Variation, MusicDiffRhythmCatalog.Defaults());
        view.Session.ModelChanged();
        var start = ScenarioNavigationTests.LogicalDescendants(view.Generation).OfType<System.Windows.Controls.Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "Music.Generation.StartPause");
        Assert.IsFalse(start.IsEnabled);
        // There are no installed runtime files in this root. Only the actual launch
        // should check them, never editor changes or rendering the workspace.
        view.Editor.Lyrics = "[Verse]\n深夜工匠坐桌旁";
        view.ConfigureGeneration(files.Root, l.T); Assert.IsTrue(start.IsEnabled);
        view.Session.ModelChanged("cuda:0; test device"); Assert.IsTrue(start.IsEnabled);
        view.Editor.Lyrics = ""; Assert.IsFalse(start.IsEnabled);
        view.Editor.Lyrics = "[Verse]\nLOPATA digs deep"; Assert.IsTrue(start.IsEnabled);
        view.Session.ModelChanged(); Assert.IsTrue(start.IsEnabled);
        view.ConfigureGeneration(files.Root, l.T); Assert.IsTrue(start.IsEnabled);
        var journal = ScenarioNavigationTests.LogicalDescendants(view.Status).OfType<System.Windows.Controls.TextBox>()
            .Single(b => AutomationProperties.GetAutomationId(b) == "Music.Status.Log").Text;
        Assert.IsFalse(journal.Contains("[Prepare]", StringComparison.Ordinal));
        Assert.IsFalse(journal.Contains("RuntimeMissing", StringComparison.Ordinal));
    }

    [STATestMethod]
    public void DiffEditorShowsOwnHintAndRestoresAceHint()
    {
        using var editor = new MusicLyricsEditor(); editor.Lyrics = "深夜工匠坐桌旁";
        foreach (var language in new[] { "ru", "en" }) {
            var l = new LocalizationService(); l.Load(language); editor.Localize(l.T);
            editor.ConfigureRequest("", "", ace: true, externalHintKey: "Music.Diff.EditorHint");
            var labels = ScenarioNavigationTests.LogicalDescendants(editor).OfType<TextBlock>().Select(t => t.Text).ToArray();
            CollectionAssert.Contains(labels, l.T("Music.Diff.EditorHint"));
            CollectionAssert.DoesNotContain(labels, l.T("Music.Ace.EditorHint"));
            Assert.IsTrue(editor.CanGenerate);
            editor.ConfigureRequest("", "", ace: true);
            CollectionAssert.Contains(ScenarioNavigationTests.LogicalDescendants(editor).OfType<TextBlock>().Select(t => t.Text).ToArray(), l.T("Music.Ace.EditorHint"));
        }
    }

    private sealed class Worker : IMusicYueWorker
    {
        public int Calls; public bool Cancel;
        public string LastRequestReceipt => "{\"fixture\":true}";
        public event Action<string>? Log { add {} remove {} }
        public Task PlanAsync(string model, MusicYueRequest request, string requestPath, string planPath, CancellationToken token) =>
            throw new AssertFailedException("DiffRhythm must not enter YuE planning.");
        public Task SynthesizeAsync(string model, string decoder, MusicYueRequest request, string requestPath, string outputPath, CancellationToken token)
        {
            Calls++; if (Cancel) throw new OperationCanceledException();
            MusicOutputTests.WriteWave(outputPath); return Task.CompletedTask;
        }
    }
}
