using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePendingWorkTests
{
    private string _folder = null!;
    private static readonly LivingArcadeModelPaths Paths = new("never-start-main.gguf", "never-start-artist.gguf");

    [TestInitialize]
    public void Initialize() => _folder = Path.Combine(Path.GetTempPath(), "lopata-arcade-pending-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        var full = Path.GetFullPath(_folder);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("lopata-arcade-pending-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    private BackgroundOperationStore BackgroundStore => new(Path.Combine(_folder, "background", "state.json"));

    private static BackgroundOperationState Work(string kind, string runId, bool paused = false, int age = 0) => new()
    {
        Kind = kind, Title = "Pending arcade test", Project = kind == LivingArcadeGeneration.WarmupKind ? null : runId,
        Phase = BackgroundOperationPhase.Waiting, UserPaused = paused,
        UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-age),
        Checkpoint = JsonSerializer.SerializeToElement(new { stage = "confirmed", sentinel = 17 }),
        Input = kind switch {
            LivingArcadeGeneration.WarmupKind => JsonSerializer.SerializeToElement(new LivingArcadeWarmupRequest(Paths, "ru")),
            LivingArcadeGeneration.FirstEventsKind => JsonSerializer.SerializeToElement(new LivingArcadeFirstEventsRequest(runId, Paths, "ru")),
            LivingArcadeGeneration.Kind => JsonSerializer.SerializeToElement(new LivingArcadeGenerationRequest(runId, 1, Paths, "ru")),
            _ => JsonSerializer.SerializeToElement(new { runId, paths = Paths })
        }
    };

    private BackgroundOperationController Reload(BackgroundOperationState selected, params BackgroundOperationState[] others)
    {
        var store = BackgroundStore;
        foreach (var state in others.Append(selected)) store.Operation(state.Id).Save(state);
        store.Save(selected);
        var controller = new BackgroundOperationController(store); controller.Load(); return controller;
    }

    [TestMethod]
    [DataRow(LivingArcadeGeneration.WarmupKind, false)]
    [DataRow(LivingArcadeGeneration.WarmupKind, true)]
    [DataRow(LivingArcadeGeneration.FirstEventsKind, false)]
    [DataRow(LivingArcadeGeneration.FirstEventsKind, true)]
    [DataRow(LivingArcadeGeneration.Kind, false)]
    [DataRow(LivingArcadeGeneration.Kind, true)]
    public void RestartFindsEachArcadeKindWithoutResumingOrChangingPauseAndCheckpoint(string kind, bool paused)
    {
        var runId = Guid.NewGuid().ToString("N");
        var original = Work(kind, runId, paused) with { Phase = BackgroundOperationPhase.Running };
        var controller = Reload(original); var executions = 0;
        controller.Register(kind, (_, _) => { executions++; return Task.CompletedTask; });
        var before = controller.State!;
        var found = LivingArcadePendingWork.Find(controller);
        Assert.IsNotNull(found); Assert.AreEqual(original.Id, found.State.Id);
        Assert.AreEqual(Paths, found.Paths);
        Assert.AreEqual(kind == LivingArcadeGeneration.WarmupKind ? null : runId, found.RunId);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, found.State.Phase);
        Assert.AreEqual(paused, found.State.UserPaused);
        Assert.IsTrue(JsonElement.DeepEquals(before.Checkpoint!.Value, controller.State!.Checkpoint!.Value));
        Assert.IsTrue(JsonElement.DeepEquals(before.Input, controller.State.Input));
        Assert.IsFalse(controller.IsRunning); Assert.AreEqual(0, executions);
        Assert.AreEqual(original.Id, LivingArcadePendingWork.Find(controller)!.State.Id, "A second page entrance must find the same operation.");
    }

    [TestMethod]
    public void SelectedOwnJobWinsOtherwiseNewestMatchingRunOrSharedWarmupWins()
    {
        var firstRun = Guid.NewGuid().ToString("N"); var secondRun = Guid.NewGuid().ToString("N");
        var selected = Work(LivingArcadeGeneration.FirstEventsKind, firstRun, age: 30);
        var newer = Work(LivingArcadeGeneration.Kind, secondRun, age: 1);
        var warm = Work(LivingArcadeGeneration.WarmupKind, firstRun, age: 10);
        var controller = Reload(selected, newer, warm);
        Assert.AreEqual(selected.Id, LivingArcadePendingWork.Find(controller)!.State.Id);
        Assert.AreEqual(newer.Id, LivingArcadePendingWork.Find(controller, secondRun)!.State.Id);
        Assert.AreEqual(warm.Id, LivingArcadePendingWork.Find(controller, Guid.NewGuid().ToString("N"))!.State.Id);
        var foreign = Work("test.foreign", firstRun);
        controller = Reload(foreign, selected, newer, warm);
        Assert.AreEqual(newer.Id, LivingArcadePendingWork.Find(controller)!.State.Id);
    }

    [TestMethod]
    public void DiscardOnlyProvenDeletedArcadeRunKeepsLiveMissingCorruptForeignAndWarmJobs()
    {
        var runs = new LivingArcadeRunStore(Path.Combine(_folder, "runs"));
        var deleted = runs.Create("Deleted"); var live = runs.Create("Live"); var corrupt = runs.Create("Corrupt");
        Assert.IsTrue(runs.Delete(deleted.Id));
        var corruptFile = Path.Combine(runs.RootDirectory, corrupt.Id, "run.json");
        File.WriteAllText(corruptFile, "{deliberately broken manifest");
        var stale = Work(LivingArcadeGeneration.FirstEventsKind, deleted.Id);
        var retained = new[] {
            Work(LivingArcadeGeneration.Kind, live.Id), Work(LivingArcadeGeneration.Kind, Guid.NewGuid().ToString("N")),
            Work(LivingArcadeGeneration.FirstEventsKind, corrupt.Id), Work("test.foreign", deleted.Id),
            Work(LivingArcadeGeneration.WarmupKind, deleted.Id)
        };
        var controller = Reload(stale, retained);
        Assert.AreEqual(1, LivingArcadePendingWork.DiscardDeleted(controller, runs));
        CollectionAssert.AreEquivalent(retained.Select(s => s.Id).ToArray(), controller.PendingOperations.Select(s => s.Id).ToArray());
        Assert.AreEqual(0, LivingArcadePendingWork.DiscardDeleted(controller, runs));
        Assert.IsNotNull(runs.Load(live.Id));
        Assert.AreEqual("{deliberately broken manifest", File.ReadAllText(corruptFile));
        var restarted = new BackgroundOperationController(BackgroundStore); restarted.Load();
        Assert.IsFalse(restarted.PendingOperations.Any(s => s.Id == stale.Id));
    }

    [TestMethod]
    public async Task TombstoneCannotDiscardAnActuallyRunningWorker()
    {
        var runs = new LivingArcadeRunStore(Path.Combine(_folder, "runs")); var run = runs.Create("Active");
        var controller = new BackgroundOperationController(BackgroundStore);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = Work(LivingArcadeGeneration.Kind, run.Id);
        var execution = controller.RunAsync(state, async _ => { ready.SetResult(); await release.Task; return 1; },
            () => Task.CompletedTask, default);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsTrue(runs.Delete(run.Id));
            Assert.AreEqual(0, LivingArcadePendingWork.DiscardDeleted(controller, runs));
            Assert.IsTrue(controller.HasLiveOperation(state.Id));
        }
        finally { release.TrySetResult(); await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public void MalformedTypedInputDoesNotInventPathsOrTargetAnUnrelatedJob()
    {
        var runId = Guid.NewGuid().ToString("N");
        var malformed = Work(LivingArcadeGeneration.Kind, runId) with { Input = JsonSerializer.SerializeToElement(new { unexpected = true }) };
        var foreign = Work("test.foreign", runId);
        var controller = Reload(malformed, foreign);
        Assert.IsNull(LivingArcadePendingWork.Find(controller));
        Assert.AreEqual(0, LivingArcadePendingWork.DiscardDeleted(controller, new(Path.Combine(_folder, "runs"))));
        Assert.AreEqual(2, controller.PendingOperations.Count);
    }
}
