using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicDiffRhythmFailureRecoveryTests
{
    [TestMethod]
    public void RestartReleasesOnlyLegacyPreflightFailureAndPreservesJobInput()
    {
        using var files = new MusicProjectTests.Files();
        var jobs = new MusicGenerationJobs(Path.Combine(files.Root, "jobs"));
        var job = jobs.Create(files.Root, Path.Combine(files.Root, "audio"), "кот", 1, 120, "rock",
            "[Slow ballad, piano, male voice]\n[Verse]\nКот уснул.\n[Final Chorus]\nТихий дом.", expert: MusicDiffRhythmCatalog.Defaults());
        var jobPath = Path.Combine(jobs.Folder(job.Id), "job.json"); var before = File.ReadAllBytes(jobPath);
        var store = new BackgroundOperationStore(Path.Combine(files.Root, "background.json"));
        var state = new BackgroundOperationState { Kind = MusicGenerationRunner.BackgroundKind, Title = "song", Project = job.Id,
            Input = JsonSerializer.SerializeToElement(new { JobId = job.Id }), Checkpoint = JsonSerializer.SerializeToElement(new { JobId = job.Id, Completed = 0 }),
            Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true, Detail = "InvalidDataException" };
        var controller = new BackgroundOperationController(store);
        void Set(BackgroundOperationState value) { store.Save(value); controller.Load(); }
        Set(state with { UserPaused = true }); Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs));
        Set(state with { Project = Guid.NewGuid().ToString("N") }); Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs));
        Set(state with { Detail = "IOException" }); Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs));
        Set(state with { Checkpoint = JsonSerializer.SerializeToElement(new { JobId = job.Id, Completed = 1 }) });
        Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs));
        var stage = Path.Combine(jobs.Folder(job.Id), "worker.json"); File.WriteAllText(stage, "{}");
        Set(state); Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs)); File.Delete(stage);
        File.WriteAllText(job.Variants[0].ResultPath, "existing result");
        Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs)); File.Delete(job.Variants[0].ResultPath);
        var valid = job with { Lyrics = "[Verse]\nКот уснул." }; jobs.Save(valid);
        Assert.IsFalse(MusicDiffRhythmFailureRecovery.Recover(controller, jobs)); jobs.Save(job);
        Assert.IsTrue(MusicDiffRhythmFailureRecovery.Recover(controller, jobs)); Assert.IsFalse(controller.HasPending);
        Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
        Assert.IsTrue(JsonElement.DeepEquals(state.Input, controller.State.Input));
        Assert.IsTrue(JsonElement.DeepEquals(state.Checkpoint!.Value, controller.State.Checkpoint!.Value));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(jobPath));
        controller.Load(); Assert.IsFalse(controller.HasPending);
    }
}
