using System.IO;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3GenerationTests
{
    private static VideoPreparationProject Project()
    {
        var p = VideoPreparationFinishTests.ReadyProject();
        var h = VideoH3WorkspaceState.Ensure(p);
        foreach (var plan in VideoH3WorkspacePlans.From(p)) h.Plans[plan.Shot.Id] = new() { ReferenceIds = [] };
        return p;
    }
    private static VideoH3Job Job(VideoPreparationProject p) => VideoH3Job.Create(p, "project.lopataAIVid", "models", 4);

    [TestMethod, DataRow(false, "review", "H3FragmentReady"),
        DataRow(true, "review", "H3FragmentsGenerated"), DataRow(true, "automatic", "H3Generated")]
    public void CompletionMessageDistinguishesSingleTakeFromFinishedProject(bool complete, string mode, string expected)
    {
        var p = Project(); p.Generator!.H3!.Settings.RunMode = mode;
        var job = Job(p); job.Run.Complete = complete;
        Assert.AreEqual(expected, job.CompletionMessageKey);
    }

    [TestMethod]
    public void HourLongJobPreservesAllPlansAndUsesPerPlanOverrides()
    {
        var p = Project(); var h = p.Generator!.H3!; h.Settings.TargetSeconds = 3600;
        var first = VideoH3WorkspacePlans.From(p)[0]; h.Plans[first.Shot.Id].Operation = "new"; h.Plans[first.Shot.Id].Seed = 137;
        h.Plans[first.Shot.Id].Prompt = "A fixed brass clock.";
        var job = Job(p);
        Assert.AreEqual(3600, job.Items.Sum(i => i.Seconds), .000001);
        Assert.AreEqual(VideoH3WorkspacePlans.From(p).Count, job.Items.Length);
        Assert.AreEqual("new", job.Items[0].Operation); Assert.AreEqual(137L, job.Items[0].Seed);
        Assert.AreEqual("A fixed brass clock.", job.Items[0].Prompt);
    }
    [TestMethod]
    public void ResumeUsesOnlyMatchingUnfinishedRunAndChangedSettingsStartNewRun()
    {
        var p = Project(); var first = Job(p); Assert.AreSame(first.Run, Job(p).Run);
        p.Generator!.H3!.Settings.ExportName = "different-export-name"; Assert.AreSame(first.Run, Job(p).Run);
        p.Generator!.H3!.Settings.Steps++; var second = Job(p); Assert.AreNotEqual(first.Run.Id, second.Run.Id);
        second.Run.Complete = true; Assert.AreNotEqual(second.Run.Id, Job(p).Run.Id);
        Assert.IsTrue(p.Generator.H3.Runs.All(r => r.Results.Count == 0));
    }
    [TestMethod, DataRow("sage"), DataRow("spectrum"), DataRow("turbo"), DataRow("bridge"), DataRow("short")]
    public void UnimplementedOptionsAreRejectedBeforeStartingAProcess(string option)
    {
        var p = Project(); var s = p.Generator!.H3!.Settings;
        switch (option) { case "sage": s.Attention = "sage"; break; case "spectrum": s.Spectrum = true; break;
            case "turbo": s.Turbo = "normal"; break; case "bridge": s.Continuation = "bridge"; break; case "short": s.ClipSeconds = 1; break; }
        Assert.ThrowsExactly<InvalidDataException>(() => Job(p)); Assert.AreEqual(0, p.Generator.H3.Runs.Count);
    }
    [TestMethod]
    public async Task PortableArchiveRestoresNativeContextWithExactNamesAndRejectsDamagedFiles()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = Project(); var job = Job(p); var run = job.Run;
        foreach (var name in new[] { "takes/first.mp4", "takes/first.mctx.safetensors", "takes/first.cond.safetensors" })
        {
            var source = f.File(name.Replace('/', '_')); var data = System.Text.Encoding.UTF8.GetBytes(name); File.WriteAllBytes(source, data);
            var role = "h3-run/" + run.Id + "/" + Guid.NewGuid().ToString("N");
            p.Attachments.Add(new(source, role)); run.Files.Add(new(name, role, data.Length, Convert.ToHexStringLower(SHA256.HashData(data))));
        }
        run.Takes.Add(new("first", job.Items[0].Id, job.Items[0].SceneId, "takes/first.mp4", 1, 5, ""));
        run.Results.Add(new(Guid.NewGuid().ToString("N"), "fragment", run.Files[0].Role, 5, DateTimeOffset.UtcNow,
            [new("test-model", "Synthetic fixture", "fixture", "test")]));
        var archive = f.File("native.lopataAIVid"); VideoPreparationArchive.Save(p, archive);
        var opened = VideoPreparationArchive.Load(archive, f.File("opened")); var loaded = opened.Generator!.H3!.Runs.Single();
        await VideoH3Runner.RestoreFiles(opened, loaded, f.File("output"), default);
        foreach (var file in loaded.Files) CollectionAssert.AreEqual(File.ReadAllBytes(opened.Attachments.Single(a => a.Role == file.Role).Path), File.ReadAllBytes(Path.Combine(f.File("output"), file.RelativePath)));
        File.AppendAllText(opened.Attachments.Single(a => a.Role == loaded.Files[1].Role).Path, "damaged");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoH3Runner.RestoreFiles(opened, loaded, f.File("second-output"), default));
    }
    [TestMethod, DataRow("null"), DataRow("path"), DataRow("hash"), DataRow("unattached")]
    public void InvalidRunCannotEnterProjectArchive(string fault)
    {
        var p = Project(); var run = Job(p).Run;
        if (fault == "null") run.Files = null!;
        else run.Files.Add(new(fault == "path" ? "../escape.mp4" : "take.mp4", "h3-run/" + run.Id + "/file", 1, fault == "hash" ? "bad" : new string('a', 64)));
        Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }

    [TestMethod]
    public async Task FullTreeAuditLeasesLongWindowsPathsAndStillRejectsTampering()
    {
        using var f = new VideoPreparationTests.TestFiles();
        using var audit = ComponentAudit.UseTestStore(f.File("audit"), enabled: true);
        var directory = Path.Combine(f.File("deep"), new string('a', 90), new string('b', 90), new string('c', 90));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "payload.txt"); File.WriteAllText(path, "original");
        var bytes = File.ReadAllBytes(path); var inventory = new[] { new PinnedTreeFile("payload.txt", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))) };
        await PinnedFileTreeVerifier.VerifyAsync(directory, inventory, default);
        File.WriteAllText(path, "modified");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(directory, inventory, default));
    }
}
