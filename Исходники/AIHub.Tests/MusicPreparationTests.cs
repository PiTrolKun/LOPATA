using System.Windows.Controls;
using System.Windows.Automation;
using System.Text.Json;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPreparationTests
{
    [TestMethod]
    public void ComponentsHavePinnedDownloadsLicensesAndCanonicalNavigation()
    {
        var cards = MusicComponentCatalog.CreateCards(Path.Combine(Path.GetTempPath(), "music-manifest"));
        Assert.HasCount(2, cards);
        Assert.AreEqual(4_340_729_408L, cards.Sum(c => c.TotalBytes));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), "unused.json");
        foreach (var card in cards)
        {
            var file = card.Files.Single();
            Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(file.Sha256.All(Uri.IsHexDigit));
            Assert.IsTrue(file.SourceUrl.Contains(MusicComponentCatalog.Revision, StringComparison.Ordinal));
            var entry = licenses.Entries.Single(e => e.Id == card.ModelArtifactId);
            Assert.IsFalse(entry.Basic);
            Assert.IsTrue(licenses.ReadText(entry.Texts.Single()).Contains("Individual creator permission", StringComparison.Ordinal));
        }
        Assert.IsFalse(MusicComponentCatalog.IsComplete([]));
        var node = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Music);
        Assert.AreEqual(ScenarioNavigationCatalog.Creation, ScenarioNavigationCatalog.Get(node.ParentId!).ParentId);
        Assert.AreEqual(node.Id, ScenarioNavigationCatalog.GetTag("music_preparation").TargetId);
        foreach (var lang in new[] { "ru", "en" })
        {
            var l = new LocalizationService(); l.Load(lang);
            foreach (var key in new[] { node.TitleKey, node.DescriptionKey, "Music.Preparation.Title", "Cloud.Tag.music_preparation" })
                Assert.AreNotEqual(key, l.T(key));
        }
    }

    [TestMethod]
    public async Task VerificationDoesNotDownloadOrAskConsentAndRefusalStopsPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-music-test-" + Guid.NewGuid().ToString("N"));
        var previous = ComponentLicenseGate.ConfirmAsync;
        var confirmations = 0;
        ComponentLicenseGate.ConfirmAsync = (_, _) => { confirmations++; throw new OperationCanceledException(); };
        try
        {
            using var service = new MusicPreparationService(new ManagedModelLibraryStore(Path.Combine(root, "library")));
            var models = Path.Combine(root, "models");
            var cards = await service.CheckAsync(models, null, CancellationToken.None);
            Assert.IsFalse(MusicComponentCatalog.IsComplete(cards));
            Assert.AreEqual(0, confirmations); Assert.IsFalse(Directory.Exists(models));
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(models, true, null, CancellationToken.None));
            Assert.AreEqual(1, confirmations); Assert.IsFalse(Directory.Exists(models));
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = previous;
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public Task MissingComponentsAndAbsentStorageCannotOpenWorkspace() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation { Complete = false };
        using var control = Create(fake);
        control.CheckAsync().GetAwaiter().GetResult();
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsFalse(control.IsWorkspace); Assert.AreEqual(0, fake.Preparations);
        control.Configure(k => k, new StorageSettings(), 1);
        control.DownloadAsync().GetAwaiter().GetResult();
        Assert.AreEqual(0, fake.Preparations);
    });

    [TestMethod]
    public Task TransitionRechecksComponentsAndStopsOnCorruption() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); using var control = Create(fake);
        control.CheckAsync().GetAwaiter().GetResult(); Assert.IsTrue(control.CanContinue);
        fake.Complete = false;
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.AreEqual(1, fake.Preparations); Assert.IsFalse(control.IsWorkspace); Assert.IsFalse(control.CanContinue);
    });

    [TestMethod]
    public Task CanceledConsentKeepsWorkspaceClosedAndAllowsRetry() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation { Decline = true }; using var control = Create(fake);
        control.CheckAsync().GetAwaiter().GetResult(); control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsFalse(control.IsWorkspace); Assert.IsFalse(control.IsBusy);
        fake.Decline = false; control.CheckAsync().GetAwaiter().GetResult(); control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsTrue(control.IsWorkspace);
    });

    [TestMethod]
    public Task PreparedWorkspaceHasPersistentEditorAndStorageChangeInvalidatesIt() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); using var control = Create(fake);
        control.CheckAsync().GetAwaiter().GetResult(); control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsTrue(control.IsWorkspace);
        var workspace = (MusicWorkspaceControl)control.Content;
        workspace.Editor.Lyrics = "Текст песни";
        control.Localize(k => k);
        Assert.AreSame(workspace, control.Content);
        Assert.AreEqual("Текст песни", workspace.Editor.Lyrics);
        Assert.IsFalse(workspace.Editor.CanGenerate); // Fake preparation has no actual tokenizer file.
        Assert.IsTrue(control.GoBack()); Assert.IsFalse(control.IsWorkspace);
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(control.IsModelSelection); Assert.IsFalse(control.GoBack());
        control.Configure(k => k, Storage("second-root"), 2);
        Assert.IsFalse(control.CanContinue);
        Assert.AreEqual("Текст песни", workspace.Editor.Lyrics);
        control.ContinueAsync().GetAwaiter().GetResult(); Assert.IsFalse(control.IsWorkspace);
    });

    [TestMethod]
    public Task CanceledOperationCannotUseItsCompletedResult() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); using var control = Create(fake);
        control.CheckAsync().GetAwaiter().GetResult();
        fake.OnPrepare = () =>
        {
            Assert.IsTrue(control.IsBusy);
            Assert.IsTrue(control.UsesArtifact(MusicComponentCatalog.ModelId));
            Assert.IsTrue(control.GoBack());
        };
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsFalse(control.IsWorkspace); Assert.IsFalse(control.CanContinue);
        Assert.IsFalse(control.IsBusy); Assert.IsFalse(control.UsesArtifact(MusicComponentCatalog.ModelId));
    });

    [TestMethod]
    public Task ModelSelectionRejectsPlaceholdersAndPreservesWorkspaceOnReturn() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); using var control = Create(fake);
        control.OpenAsync().GetAwaiter().GetResult();
        Assert.IsTrue(control.IsModelSelection); Assert.IsFalse(control.CanContinue);
        control.SelectModelAsync("ace-step", "turbo").GetAwaiter().GetResult();
        control.SelectModelAsync("yue2", "q4").GetAwaiter().GetResult();
        control.SelectModelAsync("unknown", "q8").GetAwaiter().GetResult();
        Assert.IsTrue(control.IsModelSelection); Assert.AreEqual(0, fake.Preparations);
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
        Assert.IsFalse(control.IsModelSelection); Assert.IsTrue(control.CanContinue);
        control.ContinueAsync().GetAwaiter().GetResult();
        var workspace = (MusicWorkspaceControl)control.Content;
        workspace.Editor.Lyrics = "Сохранённый текст";
        Assert.IsTrue(control.GoBack()); Assert.IsTrue(control.GoBack());
        Assert.IsFalse(control.CanContinue);
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.AreSame(workspace, control.Content); Assert.AreEqual("Сохранённый текст", workspace.Editor.Lyrics);
    });

    [TestMethod]
    public Task StudioUsesBundledRuntimeAndSharedWeightsWhenEnteringWorkspace() => ScenarioNavigationTests.Sta(() =>
    {
        var fake = new Preparation(); using var control = Create(fake);
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
        Assert.IsTrue(control.CanContinue);
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsTrue(control.IsWorkspace);
        var workspace = (MusicWorkspaceControl)control.Content;
        Assert.AreEqual(MusicStudioRuntime.Variation, workspace.Generation.Variation);
        workspace.Editor.Lyrics = "Общий текст";
        control.Localize(k => k);
        Assert.AreEqual("Общий текст", workspace.Editor.Lyrics);
    });

    [TestMethod, DataRow(BackgroundOperationPhase.Waiting), DataRow(BackgroundOperationPhase.Paused)]
    public Task PendingDiffJobOpensItsProjectWithoutSwitchingOrStarting(BackgroundOperationPhase phase) => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new MusicProjectTests.Files();
        var projects = new MusicProjects(Path.Combine(files.Root, "projects"));
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var output = new MusicOutputPreferences(Path.Combine(files.Root, "output.json"));
        MusicGenerationJob job;
        using (var original = new MusicWorkspaceControl(projects, jobs, output)) {
            original.Generation.ConfigureVariation(MusicDiffRhythmCatalog.Variation, MusicDiffRhythmCatalog.Defaults());
            original.Editor.Lyrics = "深夜工匠坐桌旁，\n旧机器忽然发亮。";
            job = jobs.Create(files.Root, files.Root, "Chinese test", 1, 30, "j-pop", original.Editor.Lyrics,
                expert: original.Generation.ExpertSettings);
            job = original.Projects.Record(job, original.Projects.Capture());
            original.Projects.Outcome(job, MusicProjectOutcome.Failed, "Previous worker failure");
        }
        var checkpoint = new BackgroundOperationStore(Path.Combine(files.Root, "operation.json"));
        checkpoint.Save(new() { Kind = MusicGenerationRunner.BackgroundKind, Title = job.Title, Project = job.Id,
            Input = JsonSerializer.SerializeToElement(new { JobId = job.Id }), Phase = phase, RequiresDecision = true });
        var controller = new BackgroundOperationController(checkpoint); controller.Load();
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = controller;
        try {
            var before = File.ReadAllText(Path.Combine(files.Root, "operation.json"));
            var fake = new Preparation();
            using var control = new MusicPreparationControl(fake, () => new MusicWorkspaceControl(projects, jobs, output));
            var l = new LocalizationService(); l.Load("ru"); control.Configure(l.T, Storage(files.Root), 1);
            control.OpenAsync().GetAwaiter().GetResult();
            Assert.IsTrue(control.IsWorkspace);
            var workspace = (MusicWorkspaceControl)control.Content;
            Assert.AreEqual(job.ProjectId, workspace.Projects.Current.Id);
            Assert.AreEqual(job.Lyrics, workspace.Editor.Lyrics);
            Assert.AreEqual(MusicDiffRhythmCatalog.Variation, workspace.Generation.Variation);
            Assert.HasCount(1, workspace.Projects.Current.Steps);
            Assert.IsTrue(workspace.Projects.Busy);
            var buttons = ScenarioNavigationTests.LogicalDescendants(workspace).OfType<Button>();
            Assert.IsTrue(buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Audio.CancelGeneration").IsEnabled);
            Assert.IsTrue(buttons.Single(b => AutomationProperties.GetAutomationId(b) == "Music.Generation.StartPause").IsEnabled);
            control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
            control.Localize(l.T);
            Assert.AreSame(workspace, control.Content);
            Assert.AreEqual(MusicDiffRhythmCatalog.Variation, workspace.Generation.Variation);
            Assert.AreEqual(0, fake.Preparations);
            Assert.AreEqual(before, File.ReadAllText(Path.Combine(files.Root, "operation.json")));
            Assert.IsTrue(controller.HasPending); Assert.IsFalse(controller.IsRunning);
            Assert.HasCount(1, projects.Load(job.ProjectId!).Steps);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    [TestMethod]
    public Task OperationAppearingDuringPreparationShowsErrorAndKeepsCheckpoint() => ScenarioNavigationTests.Sta(() =>
    {
        using var files = new MusicProjectTests.Files();
        var store = new BackgroundOperationStore(Path.Combine(files.Root, "operation.json"));
        var controller = new BackgroundOperationController(store); controller.Load();
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = controller;
        try {
            var fake = new Preparation { OnPrepare = () => {
                store.Save(new() { Kind = "other.test", Title = "Other operation", Phase = BackgroundOperationPhase.Waiting,
                    Input = JsonSerializer.SerializeToElement(new { Test = true }) });
                controller.Load();
            } };
            using var control = new MusicPreparationControl(fake, () => new MusicWorkspaceControl(
                new MusicProjects(Path.Combine(files.Root, "projects")), new MusicGenerationJobs(Path.Combine(files.Root, "jobs")),
                new MusicOutputPreferences(Path.Combine(files.Root, "output.json"))));
            var l = new LocalizationService(); l.Load("ru"); control.Configure(l.T, Storage(files.Root), 1);
            control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
            control.ContinueAsync().GetAwaiter().GetResult();
            Assert.IsFalse(control.IsWorkspace); Assert.IsFalse(control.IsBusy);
            var text = ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>();
            Assert.IsTrue(text.Any(t => t.Text.Contains(l.T("Music.Projects.Busy"), StringComparison.Ordinal)));
            control.Localize(l.T); // Rendering again must not retry the rejected switch.
            Assert.IsTrue(controller.HasPending); Assert.AreEqual("other.test", controller.State!.Kind);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    });

    private static MusicPreparationControl Create(Preparation preparation)
    {
        var control = new MusicPreparationControl(preparation);
        control.Configure(k => k, Storage("first-root"), 1); return control;
    }
    private static StorageSettings Storage(string name)
    {
        var result = new StorageSettings(); result.Models.Locations.Add(new() { Path = Path.Combine(Path.GetTempPath(), name) }); return result;
    }
    private sealed class Preparation : IMusicPreparation
    {
        public string Variation { get; set; } = MusicComponentCatalog.ModelId;
        public int MaximumParallelConnections { get; set; }
        public bool Complete { get; set; } = true;
        public bool Decline { get; set; }
        public Action? OnPrepare { get; set; }
        public int Preparations { get; private set; }
        public Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            var cards = MusicModelVariants.Cards(root, Variation);
            foreach (var c in cards) c.Status = Complete ? ManagedModelStatuses.Installed : ManagedModelStatuses.Corrupted;
            return Task.FromResult(cards);
        }
        public Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            Preparations++; OnPrepare?.Invoke();
            return Decline ? Task.FromException<IReadOnlyList<ManagedModelArtifactCard>>(new OperationCanceledException()) : CheckAsync(root, progress, token);
        }
        public void Dispose() { }
    }
}
