using System.Text.Json;
using System.Windows.Controls;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicPausedEntryTests
{
    [TestMethod]
    [DataRow("ru", BackgroundOperationPhase.Waiting)]
    [DataRow("en", BackgroundOperationPhase.Waiting)]
    [DataRow("ru", BackgroundOperationPhase.Paused)]
    [DataRow("en", BackgroundOperationPhase.Paused)]
    public Task ExplicitEntryRetriesReleaseAndPreservesPausedVideo(string language, BackgroundOperationPhase phase) => ScenarioNavigationTests.Sta(() =>
    {
        using var fixture = new Fixture(language, phase, userPaused: true);
        using var control = fixture.CreateControl();
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();
        Assert.IsTrue(control.CanContinue);
        Assert.AreEqual(0, fixture.Retirement.Calls, "Checking components must not change the saved operation.");
        Assert.AreEqual(fixture.Before, File.ReadAllText(fixture.CheckpointPath));

        control.ContinueAsync().GetAwaiter().GetResult();

        Assert.IsTrue(control.IsWorkspace);
        Assert.IsFalse(control.IsBusy);
        var workspace = (MusicWorkspaceControl)control.Content;
        Assert.AreEqual(MusicStudioRuntime.Variation, workspace.Generation.Variation);
        Assert.AreEqual("Сохранённый текст музыки", workspace.Editor.Lyrics);
        Assert.HasCount(0, workspace.Projects.Current.Steps, "Opening the workspace must not submit a generation.");
        Assert.AreEqual(1, fixture.Retirement.Calls);
        Assert.AreEqual(0, fixture.VideoResumes);
        Assert.IsFalse(fixture.Controller.BlocksNewWork);
        AssertPreservedVideo(fixture, BackgroundOperationPhase.Paused, requiresDecision: false);
        Assert.HasCount(1, fixture.Controller.PendingOperations);

        control.Localize(fixture.Localizer.T);
        Assert.AreSame(workspace, control.Content);
        Assert.AreEqual(1, fixture.Retirement.Calls, "Rendering the page must not repeat model retirement.");
    });

    [TestMethod, DataRow("ru"), DataRow("en")]
    public Task UnpausedDecisionStillBlocksEntryWithoutChangingCheckpoint(string language) => ScenarioNavigationTests.Sta(() =>
    {
        using var fixture = new Fixture(language, BackgroundOperationPhase.Waiting, userPaused: false);
        using var control = fixture.CreateControl();
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();

        control.ContinueAsync().GetAwaiter().GetResult();

        Assert.IsFalse(control.IsWorkspace);
        Assert.IsFalse(control.IsBusy);
        Assert.AreEqual(0, fixture.Retirement.Calls);
        Assert.AreEqual(0, fixture.VideoResumes);
        Assert.IsTrue(fixture.Controller.BlocksNewWork);
        Assert.AreEqual(fixture.Before, File.ReadAllText(fixture.CheckpointPath));
        AssertPreservedVideo(fixture, BackgroundOperationPhase.Waiting, requiresDecision: true);
        Assert.IsTrue(ScenarioNavigationTests.LogicalDescendants(control).OfType<TextBlock>()
            .Any(text => text.Text.Contains(fixture.Localizer.T("Music.Projects.Busy"), StringComparison.Ordinal)));
    });

    [TestMethod]
    public Task FailedRetirementKeepsEntryBlockedAndCanBeRetriedExplicitly() => ScenarioNavigationTests.Sta(() =>
    {
        using var fixture = new Fixture("ru", BackgroundOperationPhase.Waiting, userPaused: true);
        fixture.Retirement.Fail = true;
        using var control = fixture.CreateControl();
        control.SelectModelAsync("yue2", "studio-q8").GetAwaiter().GetResult();

        control.ContinueAsync().GetAwaiter().GetResult();

        Assert.IsFalse(control.IsWorkspace);
        Assert.IsFalse(control.IsBusy);
        Assert.AreEqual(1, fixture.Retirement.Calls);
        Assert.IsTrue(fixture.Controller.BlocksNewWork);
        Assert.AreEqual(fixture.Before, File.ReadAllText(fixture.CheckpointPath));
        AssertPreservedVideo(fixture, BackgroundOperationPhase.Waiting, requiresDecision: true);

        fixture.Retirement.Fail = false;
        control.CheckAsync().GetAwaiter().GetResult();
        Assert.AreEqual(1, fixture.Retirement.Calls, "A plain component check must not retry retirement.");
        control.ContinueAsync().GetAwaiter().GetResult();
        Assert.IsTrue(control.IsWorkspace);
        Assert.AreEqual(2, fixture.Retirement.Calls);
        Assert.AreEqual(0, fixture.VideoResumes);
        AssertPreservedVideo(fixture, BackgroundOperationPhase.Paused, requiresDecision: false);
    });

    private static void AssertPreservedVideo(Fixture fixture, BackgroundOperationPhase phase, bool requiresDecision)
    {
        var saved = new BackgroundOperationStore(fixture.CheckpointPath).Load()!;
        Assert.AreEqual(fixture.Initial.Id, saved.Id);
        Assert.AreEqual(fixture.Initial.Kind, saved.Kind);
        Assert.AreEqual(fixture.Initial.Project, saved.Project);
        Assert.AreEqual(fixture.Initial.Title, saved.Title);
        Assert.IsTrue(JsonElement.DeepEquals(fixture.Initial.Input, saved.Input));
        Assert.IsTrue(JsonElement.DeepEquals(fixture.Initial.Checkpoint!.Value, saved.Checkpoint!.Value));
        Assert.AreEqual(fixture.Initial.Detail, saved.Detail);
        Assert.AreEqual(fixture.Initial.UserPaused, saved.UserPaused);
        Assert.AreEqual(fixture.Initial.ElapsedSeconds, saved.ElapsedSeconds);
        Assert.AreEqual(phase, saved.Phase);
        Assert.AreEqual(requiresDecision, saved.RequiresDecision);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicProjectTests.Files _files = new();
        private readonly BackgroundOperationController? _previousController = ApplicationBackgroundOperations.Current;
        private readonly Func<IReadOnlyList<string>, CancellationToken, Task>? _previousConsent = ComponentLicenseGate.ConfirmAsync;
        public string CheckpointPath { get; }
        public BackgroundOperationState Initial { get; }
        public BackgroundOperationController Controller { get; }
        public LocalizationService Localizer { get; } = new();
        public RetirementProbe Retirement { get; } = new();
        public string Before { get; }
        public int VideoResumes { get; private set; }

        public Fixture(string language, BackgroundOperationPhase phase, bool userPaused)
        {
            CheckpointPath = Path.Combine(_files.Root, "operation.json");
            Initial = new() {
                Kind = "video.h3.generate", Title = "Paused user's video", Project = "video-project",
                Phase = phase, UserPaused = userPaused, RequiresDecision = true, ElapsedSeconds = 723.5,
                Detail = "retirement_failed: saved diagnostic",
                Input = JsonSerializer.SerializeToElement(new { ProjectId = "video-project", Seed = 2048 }),
                Checkpoint = JsonSerializer.SerializeToElement(new { CompletedPasses = new[] { 1, 2 }, NextPass = 3 })
            };
            var store = new BackgroundOperationStore(CheckpointPath); store.Save(Initial);
            Controller = new(store); Controller.Load();
            Controller.Register(Initial.Kind, (_, _) => { VideoResumes++; return Task.CompletedTask; });
            Before = File.ReadAllText(CheckpointPath);
            Localizer.Load(language);
            ApplicationBackgroundOperations.Current = Controller;
            // Preparation is fake and never asks for consent. Any incidental workspace
            // runtime readiness check must stop before models, native workers or network.
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.FromException(new OperationCanceledException("No native runtime in this UI regression."));
            ApplicationBackgroundOperations.RegisterModel(Retirement, static probe => probe.RetireAsync());
        }

        public MusicPreparationControl CreateControl()
        {
            var control = new MusicPreparationControl(new Preparation(), () => {
                var workspace = new MusicWorkspaceControl(
                    new MusicProjects(Path.Combine(_files.Root, "projects")),
                    new MusicGenerationJobs(Path.Combine(_files.Root, "jobs")),
                    new MusicOutputPreferences(Path.Combine(_files.Root, "output.json")));
                workspace.Editor.Lyrics = "Сохранённый текст музыки";
                return workspace;
            });
            var storage = new StorageSettings(); storage.Models.Locations.Add(new() { Path = Path.Combine(_files.Root, "models") });
            control.Configure(Localizer.T, storage, 1);
            return control;
        }

        public void Dispose()
        {
            // The registry holds a weak owner; a still reachable fixture must never
            // leave a failure callback behind for another test's retirement.
            Retirement.Fail = false;
            ApplicationBackgroundOperations.Current = _previousController;
            ComponentLicenseGate.ConfirmAsync = _previousConsent;
            _files.Dispose();
        }
    }

    private sealed class RetirementProbe
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task RetireAsync()
        {
            Calls++;
            return Fail ? Task.FromException(new IOException("Synthetic failed backend retirement")) : Task.CompletedTask;
        }
    }

    private sealed class Preparation : IMusicPreparation
    {
        public string Variation { get; set; } = MusicComponentCatalog.ModelId;
        public int MaximumParallelConnections { get; set; }
        public Task<IReadOnlyList<ManagedModelArtifactCard>> CheckAsync(string root, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            var cards = MusicModelVariants.Cards(root, Variation);
            foreach (var card in cards) card.Status = ManagedModelStatuses.Installed;
            return Task.FromResult(cards);
        }
        public Task<IReadOnlyList<ManagedModelArtifactCard>> PrepareAsync(string root, bool download, IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
        {
            Assert.IsFalse(download, "Opening a prepared workspace must not download anything.");
            return CheckAsync(root, progress, token);
        }
        public void Dispose() { }
    }
}
