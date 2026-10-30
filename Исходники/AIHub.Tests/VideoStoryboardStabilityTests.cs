using System.IO;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoStoryboardStabilityTests
{
    [TestMethod]
    public void ReconcileRemovesOrphansPreservesManualEditsAndArchivesPreviousBoard()
    {
        var p = VideoPreparationFinishTests.ReadyProject(); var b = p.Storyboard!;
        var old = b.Frames[0]; b.Frames[1].Seconds = 8; b.Frames[1].Notes = "Мой монтаж";
        var kept = b.Frames[1].Id; VideoStoryboard.Capture(p);
        p.Shots!.Scenes[0].Shots.RemoveAt(0);
        var added = new VideoShotPlan { Name = "New shot", Start = "A new action begins", End = "The action ends", Duration = 4 };
        p.Shots.Scenes[0].Shots.Add(added);
        VideoStoryboard.Synchronize(p);
        Assert.HasCount(2, b.Frames); Assert.AreEqual(kept, b.Frames[0].Id);
        Assert.AreEqual(8, b.Frames[0].Seconds); Assert.AreEqual("Мой монтаж", b.Frames[0].Notes);
        Assert.AreEqual(added.Id, b.Frames[1].ShotId); Assert.IsTrue(VideoPreparationReadiness.BoardReady(p));
        Assert.IsTrue(VideoStoryboard.MoveVersion(p, -1)); Assert.IsTrue(b.Frames.Any(f => f.Id == old.Id));
        Assert.IsTrue(VideoStoryboard.MoveVersion(p, 1)); Assert.IsFalse(b.Frames.Any(f => f.Id == old.Id));
        using var files = new VideoPreparationTests.TestFiles(); var path = files.File("board.lopataAIVid");
        VideoPreparationArchive.Save(p, path); var loaded = VideoPreparationArchive.Load(path, files.File("opened"));
        Assert.AreEqual("Мой монтаж", loaded.Storyboard!.Frames[0].Notes); loaded.Validate();
    }
    [TestMethod]
    public void ImportRepairsOldOrphanBoardOnceAndExplicitOrderPreservesTiming()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        p.Storyboard!.Frames[0].Seconds = 12; p.Storyboard.Frames.Reverse();
        p.Storyboard.Frames.Add(new() { ShotId = Guid.NewGuid().ToString("N"), Seconds = 30 });
        var path = files.File("old.lopataAIVid"); VideoPreparationArchive.Save(p, path);
        var session = new VideoPreparationSession(files.File("recovery")); session.Load(path);
        Assert.IsTrue(session.IsDirty); Assert.HasCount(2, session.Project.Storyboard!.Frames);
        VideoStoryboard.Synchronize(session.Project, restorePlanOrder: true);
        Assert.AreEqual(session.Project.Shots!.Scenes[0].Shots[0].Id, session.Project.Storyboard.Frames[0].ShotId);
        Assert.AreEqual(12, session.Project.Storyboard.Frames[0].Seconds);
        session.Checkpoint(); Assert.IsFalse(session.IsDirty);
        Assert.IsTrue(VideoStoryboard.MoveVersion(session.Project, -1));
        var version = session.Project.Storyboard.RevisionId; VideoStoryboard.Ensure(session.Project);
        Assert.AreEqual(version, session.Project.Storyboard.RevisionId, "Rendering undo must not immediately replace the selected history version.");
    }
    [TestMethod]
    public void NewFrameDoesNotPickFirstReferenceOrReplaceExplicitPicture()
    {
        var p = VideoPreparationFinishTests.ReadyProject(); var shot = p.Shots!.Scenes[0].Shots[0];
        shot.ReferenceIds = [Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")];
        p.Storyboard = null; var b = VideoStoryboard.Ensure(p);
        Assert.IsEmpty(b.Frames[0].ImageId);
        b.Frames[0].ImageId = shot.ReferenceIds[1]; VideoStoryboard.Synchronize(p);
        Assert.AreEqual(shot.ReferenceIds[1], b.Frames[0].ImageId);
    }
    [TestMethod]
    public void DuplicateGuardAndReviewEnvelopeRejectBadResults()
    {
        var scene = new VideoScenePlan { Shots = [new() { Name = "A", Start = "Two figures emerge", End = "They face each other" }] };
        scene.Shots.Add(VideoShots.Copy(scene.Shots[0])); Assert.IsTrue(VideoShotsQuality.HasDuplicates(scene));
        scene.Shots[1].End = "The figures turn around"; Assert.IsFalse(VideoShotsQuality.HasDuplicates(scene));
        Assert.IsEmpty(VideoShotsQuality.Read("{\"valid\":true,\"issues\":[]}"));
        Assert.HasCount(1, VideoShotsQuality.Read("{\"valid\":false,\"issues\":[\"A character is forbidden and visible\"]}"));
        foreach (var text in new[] { "{}", "{\"valid\":false,\"issues\":[]}", "{\"valid\":true,\"issues\":[\"conflict\"]}", "[]", "broken" })
            Assert.ThrowsExactly<InvalidDataException>(() => VideoShotsQuality.Read(text));
    }
    [TestMethod]
    public async Task RejectedSceneIsCorrectedOnceAndReviewIsDurable()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoShotsTests.Root();
        var job = VideoShotsTests.Job(p, files.File("review.lopataAIVid")); var reviews = 0; var corrections = 0;
        var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) => {
            if (r.Purpose == VideoShotsQuality.Purpose && ++reviews == 1)
                return Task.FromResult(new VideoConceptAnswer("{\"valid\":false,\"issues\":[\"The shot forbids its own visible character\"]}", "", "", VideoConceptTests.Model));
            if (r.Purpose == VideoShotsProtocol.Scene && r.TargetText.Length > 0) corrections++;
            return Task.FromResult(VideoShotsTests.Answer(r)); } };
        var receipts = new VideoConceptReceiptStore(files.File("receipts"));
        await VideoShotsWork.RunAsync(job, p, assistant, receipts, () => Task.CompletedTask, null, null, default);
        Assert.AreEqual(1, corrections); Assert.AreEqual(7, assistant.Calls);
        Assert.HasCount(2, p.Shots!.Candidates[0].ReviewedScenes); Assert.IsTrue(p.Shots.Candidates[0].Complete);
        await VideoShotsWork.RunAsync(job, p, assistant, receipts, () => Task.CompletedTask, null, null, default);
        Assert.AreEqual(7, assistant.Calls); p.Validate();
    }
    [TestMethod]
    public async Task PersistentConflictStopsWithoutReplacingAcceptedSceneOrAuditAdvice()
    {
        using var files = new VideoPreparationTests.TestFiles(); var p = VideoShotsTests.Root();
        p.Shots = new() { Scenes = [new() { Name = "My accepted scene", Shots = [new() { Name = "Manual shot", Start = "First action", End = "Last action" }] }] };
        var job = VideoShotsTests.Job(p, files.File("conflict.lopataAIVid")); var assistant = new VideoConceptTests.FakeAssistant { Run = (r, _) =>
            Task.FromResult(r.Purpose == VideoShotsQuality.Purpose ? new VideoConceptAnswer("{\"valid\":false,\"issues\":[\"Contradictory ending\"]}", "", "", VideoConceptTests.Model) : VideoShotsTests.Answer(r)) };
        var error = await Assert.ThrowsExactlyAsync<AIHub.Models.BackgroundOperationFailedException>(() =>
            VideoShotsWork.RunAsync(job, p, assistant, new(files.File("receipts")), () => Task.CompletedTask, null, null, default));
        Assert.AreEqual("VideoPrep.ShotsQualityFailed", error.Message); Assert.AreEqual(5, assistant.Calls);
        Assert.AreEqual("My accepted scene", p.Shots.Scenes[0].Name); Assert.IsFalse(AIHub.Services.ComponentAuditFailures.IsEligible(error));
    }
}
