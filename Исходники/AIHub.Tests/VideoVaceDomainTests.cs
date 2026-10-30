using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVaceDomainTests
{
    private static VideoVaceJob Job(VideoPreparationProject p, string archive = "project.lopataAIVid", string models = "models") =>
        VideoVaceJob.Create(p, archive, models, 4);

    [TestMethod]
    public void DefaultsAndPhysicalProbeGridsRemainIndependentFromH3AndGeneric24FpsChoice()
    {
        var p = VideoPreparationFinishTests.ReadyProject();
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, 170, "existing H3"));
        var h3 = VideoH3WorkspaceState.Ensure(p); h3.Settings.Width = 480; h3.Settings.Height = 864; h3.Settings.Seed = 42;
        var before = JsonSerializer.Serialize(h3);
        var state = VideoVaceWorkspaceState.FromProject(p);
        Assert.AreEqual(832, state.Settings.Width); Assert.AreEqual(480, state.Settings.Height); Assert.AreEqual(-1L, state.Settings.Seed);
        Assert.AreEqual(81, state.Settings.Frames); Assert.AreEqual(9, state.Settings.ContextFrames);
        Assert.AreEqual(0d, state.Settings.TargetSeconds); Assert.AreEqual(0, state.Runs.Count); Assert.IsNull(p.Generator!.Vace);
        foreach (var (width, height) in new[] { (832, 480), (480, 832), (320, 192), (416, 240) })
        {
            state.Settings.Width = width; state.Settings.Height = height; state.Settings.Frames = 17; state.Settings.ContextFrames = 5;
            VideoVaceWorkspaceState.Validate(state);
        }
        _ = Job(p); Assert.AreEqual(before, JsonSerializer.Serialize(h3));
        var copied = VideoVaceWorkspaceState.FromProject(p); copied.Settings.Seed = 999; copied.Runs.Clear();
        Assert.AreEqual(-1L, p.Generator.Vace!.Settings.Seed); Assert.HasCount(1, p.Generator.Vace.Runs);
    }

    [TestMethod, DataRow("dimensions"), DataRow("frame-grid"), DataRow("one-frame-context"), DataRow("no-delivery"),
        DataRow("nan-duration"), DataRow("memory"), DataRow("reserve"), DataRow("attempts"), DataRow("output-size"), DataRow("filename")]
    public void UnsupportedOrNonFiniteSettingsCannotCreateAJob(string fault)
    {
        var p = VideoPreparationFinishTests.ReadyProject(); var s = VideoVaceWorkspaceState.Ensure(p).Settings;
        switch (fault)
        {
            case "dimensions": s.Width = 833; break;
            case "frame-grid": s.Frames = 80; break;
            case "one-frame-context": s.ContextFrames = 1; break;
            case "no-delivery": s.ContextFrames = 81; break;
            case "nan-duration": s.TargetSeconds = double.NaN; break;
            case "memory": s.MemoryMode = "cuda:0"; break;
            case "reserve": s.VramReserveGb = double.PositiveInfinity; break;
            case "attempts": s.RetryCount = 0; break;
            case "output-size": s.OutputHeight = 2160; break;
            case "filename": s.ExportName = "../outside"; break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => Job(p)); Assert.IsEmpty(p.Generator!.Vace!.Runs);
    }

    [TestMethod]
    public void StoryboardOrderCurrentTranslationAndApproximateHourLengthArePreserved()
    {
        var p = VideoPreparationFinishTests.ReadyProject(); var frames = p.Storyboard!.Frames;
        var originalFirst = frames[0]; frames.Reverse(); frames[0].Seconds = 7; frames[1].Seconds = 11;
        var scene = p.Shots!.Scenes[0];
        var working = VideoShots.SelectedTranslation(p.Shots, scene.Id)!;
        working.Scene.Shots[0].Start = "CURRENT ENGLISH TRANSLATION";
        var job = Job(p);
        CollectionAssert.AreEqual(frames.Select(f => f.ShotId).ToArray(), job.Items.Select(i => i.Id).ToArray());
        Assert.AreEqual(18d, job.Items.Sum(i => i.Seconds));
        StringAssert.Contains(job.Items.Single(i => i.Id == originalFirst.ShotId).Prompt, "CURRENT ENGLISH TRANSLATION");
        p.Generator!.Vace!.Settings.TargetSeconds = 3600;
        var hour = Job(p); Assert.AreEqual(3600d, hour.Items.Sum(i => i.Seconds), .0000001);
        Assert.AreEqual(job.Items.Length, hour.Items.Length);
        scene.Shots[0].Start = "Updated native action";
        var stale = Job(p); StringAssert.Contains(stale.Items.Single(i => i.Id == originalFirst.ShotId).Prompt, "Updated native action");
        Assert.IsFalse(stale.Items.Single(i => i.Id == originalFirst.ShotId).Prompt.Contains("CURRENT ENGLISH TRANSLATION", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OnlyOneReferenceIsUsedAndTheStoryboardChoiceWins()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        var first = AddImage(p, f.File("first.png"), "first-image"); var second = AddImage(p, f.File("second.png"), "second-image");
        var chosen = AddImage(p, f.File("chosen.png"), "chosen-image");
        p.Shots!.Scenes[0].Shots[0].ReferenceIds = [first.Id, second.Id];
        p.Storyboard!.Frames[0].ImageId = chosen.Id;
        var job = Job(p); Assert.HasCount(1, job.Items[0].Images); Assert.AreEqual(VideoReferences.PathFor(p, chosen), job.Items[0].Images[0]);
        File.AppendAllText(VideoReferences.PathFor(p, second), "not selected");
        Assert.AreSame(job.Run, Job(p).Run, "Unused images must not become hidden model inputs.");
    }

    [TestMethod]
    public void HashUsesReferenceContentsAndInferenceParametersButNotHostPathsNamesOrRetryBudget()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        var image = AddImage(p, f.File("original.png"), "same reference bytes"); p.Storyboard!.Frames[0].ImageId = image.Id;
        var first = Job(p, "first.lopataAIVid", "first-model-location");
        var relocated = f.File("moved.png"); File.Copy(VideoReferences.PathFor(p, image), relocated);
        var index = p.Attachments.FindIndex(a => a.Role == image.AttachmentRole); p.Attachments[index] = new(relocated, image.AttachmentRole);
        var s = p.Generator!.Vace!.Settings; s.ExportName = "renamed"; s.RetryCount = 5;
        Assert.AreSame(first.Run, Job(p, "relocated.lopataAIVid", "other-model-location").Run);
        File.AppendAllText(relocated, "changed pixels"); var changedReference = Job(p); Assert.AreNotEqual(first.Run.Id, changedReference.Run.Id);
        s.Steps++; Assert.AreNotEqual(changedReference.Run.Id, Job(p).Run.Id);
        Assert.AreEqual(64, first.Run.InputHash.Length); Assert.IsTrue(first.Run.InputHash.All(char.IsAsciiHexDigit));
    }

    [TestMethod]
    public void CreateReusesOnlyUnfinishedMatchingRunAndJsonRetainsScheduleAndCheckpointIdentity()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); var first = Job(p);
        var run = first.Run; PopulateCheckpoint(p, run, f, complete: true);
        var next = Job(p); Assert.AreNotEqual(run.Id, next.Run.Id); Assert.AreEqual(run.InputHash, next.Run.InputHash);
        Assert.AreSame(next.Run, Job(p).Run);
        var scheduled = next with { ScheduledUtc = DateTimeOffset.UtcNow.AddHours(1) };
        var recovered = JsonSerializer.Deserialize<VideoVaceJob>(JsonSerializer.Serialize(scheduled))!;
        Assert.AreEqual(scheduled.ScheduledUtc, recovered.ScheduledUtc); Assert.AreEqual(next.Run.Id, recovered.Run.Id);
        Assert.IsFalse(recovered.Run.Complete);
    }

    [TestMethod]
    public void ArchiveValidationNeedsPortableRolesAndLineageButDoesNotTouchFilesystem()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); var run = Job(p).Run;
        PopulateCheckpoint(p, run, f, complete: true);
        p.Attachments = p.Attachments.Select((a, i) => new VideoPreparationAttachment("media/" + i.ToString("D5") + ".bin", a.Role)).ToList();
        VideoVaceRun.Validate(p, [run]); VideoVaceWorkspaceState.Validate(p.Generator!.Vace);
        Assert.AreEqual((81 + 81 - 9) / 16d, run.Takes.Sum(t => t.DeliveredSeconds));
        Assert.AreEqual(4.5d, run.Takes[1].Seconds); Assert.AreEqual(run.Takes[0].Id, run.Takes[1].Parent);
        Assert.AreEqual(81, run.Takes[1].Frames, "The archived native video includes its conditioned prefix.");
    }

    [TestMethod, DataRow("path"), DataRow("hash"), DataRow("role"), DataRow("duplicate-role"), DataRow("missing-role"),
        DataRow("parent"), DataRow("prefix"), DataRow("grid"), DataRow("lineage"), DataRow("nan-time"), DataRow("selected-result"), DataRow("null-list")]
    public void CorruptedCheckpointMetadataIsRejectedWithoutRestoringAWorker(string fault)
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); var run = Job(p).Run;
        PopulateCheckpoint(p, run, f, complete: true); var s = p.Generator!.Vace!;
        switch (fault)
        {
            case "path": run.Files[0] = run.Files[0] with { RelativePath = "../outside.mp4" }; break;
            case "hash": run.Files[0] = run.Files[0] with { Sha256 = "wrong" }; break;
            case "role": run.Files[0] = run.Files[0] with { Role = "h3-run/" + run.Id + "/file" }; break;
            case "duplicate-role": p.Attachments.Add(p.Attachments[0]); break;
            case "missing-role": p.Attachments.RemoveAt(0); break;
            case "parent": run.Takes[1] = run.Takes[1] with { Parent = "unknown" }; break;
            case "prefix": run.Takes[1] = run.Takes[1] with { PrefixFrames = 1 }; break;
            case "grid": run.Takes[1] = run.Takes[1] with { Frames = 80 }; break;
            case "lineage": run.Files.RemoveAt(1); break;
            case "nan-time": run.GenerationSeconds = double.NaN; break;
            case "selected-result": s.SelectedResultId = Guid.NewGuid().ToString("N"); break;
            case "null-list": run.Takes = null!; break;
        }
        Assert.ThrowsExactly<InvalidDataException>(() => { VideoVaceWorkspaceState.Validate(s); VideoVaceRun.Validate(p, s.Runs); });
    }

    [TestMethod]
    public async Task PortableArchiveRestoresAllNativePrefixAndLineageFilesAndRejectsModifiedPayload()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject(); var run = Job(p).Run;
        PopulateCheckpoint(p, run, f, complete: true); p.Generator!.Vace!.SelectedResultId = run.Results.Single().Id;
        var archive = f.File("vace.lopataAIVid"); VideoPreparationArchive.Save(p, archive);
        var loaded = VideoPreparationArchive.Load(archive, f.File("opened")); var restored = loaded.Generator!.Vace!.Runs.Single();
        Assert.AreEqual(run.InputHash, restored.InputHash); Assert.AreEqual(run.GenerationSeconds, restored.GenerationSeconds);
        Assert.AreEqual(run.Takes[0].Id, restored.Takes[1].Parent); Assert.AreEqual(run.Results.Single().Id, loaded.Generator.Vace.SelectedResultId);
        await VideoVaceRunner.RestoreFiles(loaded, restored, f.File("output"), default);
        foreach (var file in restored.Files)
            CollectionAssert.AreEqual(File.ReadAllBytes(loaded.Attachments.Single(a => a.Role == file.Role).Path), File.ReadAllBytes(Path.Combine(f.File("output"), file.RelativePath)));
        File.AppendAllText(loaded.Attachments.Single(a => a.Role == restored.Files[0].Role).Path, "modified");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoVaceRunner.RestoreFiles(loaded, restored, f.File("different-output"), default));
    }

    private static VideoReferenceImage AddImage(VideoPreparationProject p, string path, string bytes)
    {
        File.WriteAllText(path, bytes); var image = new VideoReferenceImage { Name = Path.GetFileName(path), Width = 1, Height = 1 };
        image.AttachmentRole = "video-reference/" + image.Id; p.Attachments.Add(new(path, image.AttachmentRole)); VideoReferences.Ensure(p).Images.Add(image); return image;
    }

    private static void PopulateCheckpoint(VideoPreparationProject p, VideoVaceRun run, VideoPreparationTests.TestFiles f, bool complete)
    {
        var item = p.Storyboard!.Frames[0].ShotId;
        var first = new VideoVaceTake("first", item, "takes/first.mp4", 51, 81, 0, "");
        var second = new VideoVaceTake("second", item, "takes/second.mp4", 52, 81, 9, first.Id);
        foreach (var take in new[] { first, second })
        {
            AddFile(take.Video, "synthetic native video " + take.Id);
            AddFile(Path.ChangeExtension(take.Video, ".lineage.json").Replace('\\', '/'), JsonSerializer.Serialize(new
                { Run = run.Id, run.InputHash, Take = take, Fps = 16, ContextMode = "native-vace-firstclip-mask" }));
            run.Takes.Add(take);
        }
        AddFile("whole.mp4", "synthetic whole video");
        run.Results.Add(new(Guid.NewGuid().ToString("N"), run.Files.Last().Role, run.Takes.Sum(t => t.DeliveredSeconds), DateTimeOffset.UtcNow,
            [new(VideoVaceCatalog.ComponentId, "Synthetic VACE fixture", VideoVaceCatalog.Revision, "test fixture")]));
        run.GenerationSeconds = 128.25; run.Complete = complete;
        void AddFile(string name, string text)
        {
            var source = f.File(name.Replace('/', '_')); File.WriteAllText(source, text); var data = File.ReadAllBytes(source);
            var role = "vace-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
            p.Attachments.Add(new(source, role)); run.Files.Add(new(name, role, data.Length, Convert.ToHexStringLower(SHA256.HashData(data))));
        }
    }
}
