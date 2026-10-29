using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3FinalizationRecoveryTests
{
    private static (VideoPreparationProject Project, VideoH3Job Recorded, VideoH3Job Fresh) Fixture(VideoPreparationTests.TestFiles files)
    {
        var project = VideoPreparationFinishTests.ReadyProject();
        var state = VideoH3WorkspaceState.Ensure(project);
        foreach (var plan in VideoH3WorkspacePlans.From(project)) state.Plans[plan.Shot.Id] = new() { ReferenceIds = [] };
        var recorded = VideoH3Job.Create(project, files.File("project.lopataAIVid"), files.File("models"), 4);
        recorded.Run.InputHash = new string('a', 64); recorded.Run.GenerationSeconds = 100;
        foreach (var item in recorded.Items)
        {
            var id = Guid.NewGuid().ToString("N"); var video = "takes/" + id + ".mp4";
            recorded.Run.Takes.Add(new(id, item.Id, item.SceneId, video, 1, item.Seconds,
                recorded.Run.Takes.LastOrDefault()?.Video ?? ""));
            foreach (var extension in new[] { ".mp4", ".mctx.safetensors", ".cond.safetensors" })
            {
                var relative = "takes/" + id + extension; var source = files.File(id + extension);
                var bytes = System.Text.Encoding.UTF8.GetBytes(relative); File.WriteAllBytes(source, bytes);
                var role = "h3-run/" + recorded.Run.Id + "/" + Guid.NewGuid().ToString("N");
                project.Attachments.Add(new(source, role));
                recorded.Run.Files.Add(new(relative, role, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
        }
        return (project, recorded, VideoH3Job.Create(project, files.File("recovered.lopataAIVid"), files.File("models"), 4));
    }

    [TestMethod]
    public async Task RepairCreatesNewIdentityAndReusesOnlyVerifiedOriginalTakes()
    {
        using var files = new VideoPreparationTests.TestFiles(); var (project, recorded, fresh) = Fixture(files);
        var recovered = await VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default);
        Assert.IsTrue(recovered.FinalizeOnly); Assert.AreEqual(recorded.Run.Id, recovered.Run.SourceRunId);
        Assert.AreNotEqual(recorded.Run.InputHash, recovered.Run.InputHash);
        Assert.AreEqual(100d, recovered.Run.GenerationSeconds); Assert.AreEqual(0, recovered.Run.RetryAttempts);
        CollectionAssert.AreEqual(recorded.Run.Takes, recovered.Run.Takes);
        Assert.AreEqual(recorded.Run.Files.Count, recovered.Run.Files.Count);
        Assert.IsTrue(recovered.Run.Files.All(f => f.Role.StartsWith("h3-run/" + fresh.Run.Id + "/", StringComparison.Ordinal)));
        project.Validate();
        await VideoH3Runner.RestoreFiles(project, recovered.Run, files.File("restored"), default);
        foreach (var artifact in recovered.Run.Files) Assert.IsTrue(File.Exists(Path.Combine(files.File("restored"), artifact.RelativePath)));
        Assert.IsFalse(recorded.Run.Complete); Assert.AreEqual(new string('a', 64), recorded.Run.InputHash);
    }

    [TestMethod, DataRow("settings"), DataRow("prompt"), DataRow("hash"), DataRow("partial"), DataRow("corruption"), DataRow("missing-context")]
    public async Task ChangedInputsIncompleteCoverageAndCorruptionNeverTransfer(string change)
    {
        using var files = new VideoPreparationTests.TestFiles(); var (project, recorded, fresh) = Fixture(files);
        switch (change)
        {
            case "settings": fresh.Settings.Steps++; break;
            case "prompt": fresh.Items[0] = fresh.Items[0] with { Prompt = "A changed request" }; break;
            case "hash": recorded = recorded with { Run = new() { Id = recorded.Run.Id, InputHash = new string('b', 64) } }; break;
            case "partial": recorded.Run.Takes.RemoveAt(recorded.Run.Takes.Count - 1); break;
            case "corruption": File.AppendAllText(project.Attachments[0].Path, "changed"); break;
            case "missing-context": recorded.Run.Files.RemoveAll(f => f.RelativePath.EndsWith(".mctx.safetensors", StringComparison.Ordinal)); break;
        }
        var count = project.Attachments.Count;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default));
        Assert.AreEqual(count, project.Attachments.Count); Assert.AreEqual(0, fresh.Run.Files.Count); Assert.AreEqual(0, fresh.Run.Takes.Count);
    }

    [TestMethod]
    public async Task CommittedTransferCanResumeBeforeControllerInputWasReplaced()
    {
        using var files = new VideoPreparationTests.TestFiles(); var (project, recorded, fresh) = Fixture(files);
        var first = await VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default);
        first.Run.GenerationSeconds = 125;
        var count = project.Attachments.Count;
        var resumed = await VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default);
        Assert.AreSame(first.Run, resumed.Run); Assert.IsTrue(resumed.FinalizeOnly);
        Assert.AreEqual(125d, resumed.Run.GenerationSeconds); Assert.AreEqual(count, project.Attachments.Count);
        resumed.Run.Files[0] = resumed.Run.Files[0] with { Sha256 = new string('0', 64) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default));
    }

    [TestMethod]
    public async Task PortableReferencePathsCanChangeButTheirContentsCannot()
    {
        using var files = new VideoPreparationTests.TestFiles(); var (project, recorded, fresh) = Fixture(files);
        var first = files.File("first.png"); var second = files.File("portable.png");
        File.WriteAllText(first, "original image"); File.Copy(first, second);
        recorded.Items[0] = recorded.Items[0] with { Images = [first] };
        fresh.Items[0] = fresh.Items[0] with { Images = [second] };
        var changed = fresh with { Items = fresh.Items.Select(i => i with { Images = i.Images.ToArray() }).ToArray() };
        File.WriteAllText(second, "changed image");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoH3FinalizationRecovery.CreateAsync(recorded, changed, project, default));
        File.Copy(first, second, true);
        Assert.IsTrue((await VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default)).FinalizeOnly);
    }

    [TestMethod, Timeout(28800000)]
    public async Task ExplicitArchiveRecoveryFinalizesSavedTakesWithoutRegeneration()
    {
        var sourceArchive = Environment.GetEnvironmentVariable("LOPATA_H3_RECOVERY_ARCHIVE");
        var operationPath = Environment.GetEnvironmentVariable("LOPATA_H3_RECOVERY_OPERATION");
        var directory = Environment.GetEnvironmentVariable("LOPATA_H3_RECOVERY_DIRECTORY");
        if (sourceArchive is null || operationPath is null || directory is null)
        { Assert.Inconclusive("Opt-in finalization of a separately saved project copy."); return; }
        Directory.CreateDirectory(directory);
        using var source = File.OpenRead(sourceArchive); var originalHash = SHA256.HashData(source); source.Dispose();
        var operation = new BackgroundOperationStore(operationPath).Load()!;
        var recorded = operation.Input.Deserialize<VideoH3Job>()!;
        Assert.AreEqual(Path.GetFullPath(sourceArchive), Path.GetFullPath(recorded.ArchivePath));
        var project = VideoPreparationArchive.Load(sourceArchive, Path.Combine(directory, "opened-" + Guid.NewGuid().ToString("N")));
        var previous = project.Generator!.H3!.Runs.Single(r => r.Id == recorded.Run.Id);
        var oldIds = previous.Takes.Select(t => t.Id).ToArray();
        var archive = Path.Combine(directory, "recovered.lopataAIVid");
        if (File.Exists(archive))
        {
            var checkpoint = VideoPreparationArchive.Load(archive, Path.Combine(directory, "resumed-" + Guid.NewGuid().ToString("N")));
            var candidate = VideoH3Job.Create(checkpoint, archive, recorded.ModelsRoot, recorded.Connections);
            if (candidate.Run.SourceRunId == previous.Id && !candidate.Run.Complete)
                project = checkpoint;
        }
        var fresh = VideoH3Job.Create(project, archive, recorded.ModelsRoot, recorded.Connections);
        var recovered = await VideoH3FinalizationRecovery.CreateAsync(recorded, fresh, project, default);
        VideoPreparationArchive.Save(project, archive);
        var logGate = new object();
        void Log(string line)
        {
            lock (logGate) File.AppendAllText(Path.Combine(directory, "recovery.log"), DateTimeOffset.UtcNow.ToString("O") + " " + line + "\n");
        }
        using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), enabled: false);
        await VideoH3Runner.RunAsync(recovered, project, () => { VideoPreparationArchive.Save(project, archive); return Task.CompletedTask; },
            Log, default, library: new ManagedModelLibraryStore(Path.Combine(directory, "library")));
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(recovered.Run));
        Assert.IsTrue(recovered.Run.Complete); CollectionAssert.AreEqual(oldIds, recovered.Run.Takes.Select(t => t.Id).ToArray());
        Assert.AreEqual(1, recovered.Run.Results.Count);
        var estimate = recovered.Items.Sum(i => i.Seconds);
        var seconds = recovered.Run.Results.Single().Seconds;
        Assert.IsTrue(seconds >= estimate - .1 && seconds <= estimate + VideoH3WorkspaceState.RawFrames(recovered.Settings.ClipSeconds) / 24d);
        Assert.IsTrue(recovered.Run.GenerationSeconds >= previous.GenerationSeconds);
        using var unchanged = File.OpenRead(sourceArchive); CollectionAssert.AreEqual(originalHash, SHA256.HashData(unchanged));
    }

    [TestMethod, Timeout(600000)]
    public async Task InstallCompletedRecoveryIntoOriginalProject()
    {
        var directory = Environment.GetEnvironmentVariable("LOPATA_H3_INSTALL_RECOVERY");
        var archiveBackup = Environment.GetEnvironmentVariable("LOPATA_H3_INSTALL_ARCHIVE_BACKUP");
        var operationPath = Environment.GetEnvironmentVariable("LOPATA_H3_INSTALL_OPERATION");
        var operationBackup = Environment.GetEnvironmentVariable("LOPATA_H3_INSTALL_OPERATION_BACKUP");
        if (directory is null || archiveBackup is null || operationPath is null || operationBackup is null)
        { Assert.Inconclusive("Explicit opt-in installation of a verified completed recovery."); return; }
        AssertApplicationClosed();
        var evidence = Path.Combine(directory, "install-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(evidence);
        var expected = new BackgroundOperationStore(operationBackup).Load()!;
        var store = new BackgroundOperationStore(operationPath); var operation = store.Load()!;
        var recorded = expected.Input.Deserialize<VideoH3Job>()!;
        var current = operation.Input.Deserialize<VideoH3Job>()!;
        Assert.AreEqual(expected.Id, operation.Id); Assert.AreEqual(VideoH3Job.Kind, operation.Kind);
        Assert.IsTrue(operation.Phase is BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Paused);
        Assert.AreEqual(recorded.Run.Id, current.Run.Id); Assert.AreEqual(recorded.Run.InputHash, current.Run.InputHash);
        Assert.AreEqual(recorded.ProjectId, current.ProjectId);
        var original = recorded.ArchivePath;
        var baseline = await FileHash(archiveBackup); CollectionAssert.AreEqual(baseline, await FileHash(original));
        var operationHash = await FileHash(operationPath);
        var before = VideoPreparationArchive.Load(original, Path.Combine(evidence, "before"));
        var recovered = VideoPreparationArchive.Load(Path.Combine(directory, "recovered.lopataAIVid"), Path.Combine(evidence, "recovered"));
        Assert.AreEqual(before.Id, recovered.Id); Assert.AreEqual(recorded.ProjectId, recovered.Id);
        var prior = before.Generator!.H3!.Runs.Single(r => r.Id == recorded.Run.Id);
        var finished = recovered.Generator!.H3!.Runs.Single(r => r.SourceRunId == prior.Id && r.Complete);
        Assert.AreNotEqual(prior.InputHash, finished.InputHash); Assert.AreEqual(8, prior.Takes.Count);
        Assert.AreEqual(1, finished.Results.Count); Assert.AreEqual("whole", finished.Results.Single().Kind);
        Assert.AreEqual(JsonSerializer.Serialize(prior.Takes), JsonSerializer.Serialize(finished.Takes));
        Assert.AreEqual(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(recovered.Generator.H3.Runs.Single(r => r.Id == prior.Id)));
        Assert.IsTrue(finished.GenerationSeconds >= prior.GenerationSeconds);
        foreach (var artifact in prior.Files)
        {
            var retained = finished.Files.Single(f => f.RelativePath == artifact.RelativePath);
            Assert.AreEqual(artifact.Sha256, retained.Sha256); Assert.AreEqual(artifact.Size, retained.Size);
        }
        foreach (var attachment in before.Attachments)
        {
            var retained = recovered.Attachments.Single(a => a.Role == attachment.Role);
            CollectionAssert.AreEqual(await FileHash(attachment.Path), await FileHash(retained.Path));
        }
        // Compare every preparation field independently of archive export tags,
        // portable attachment paths and the explicitly added generation record.
        var normalized = recovered.Clone(); normalized.Provenance = before.Provenance;
        normalized.Attachments = before.Attachments;
        normalized.Generator!.H3!.Runs = before.Generator.H3.Runs;
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(normalized));

        var result = finished.Results.Single();
        var path = recovered.Attachments.Single(a => a.Role == result.VideoRole).Path;
        using var probe = await AIHub.Services.VideoEditor.VideoRuntime.ProbeAsync(path, default);
        var format = probe.RootElement.GetProperty("format"); var tags = format.GetProperty("tags");
        var facts = JsonSerializer.Deserialize<OutputProvenance>(tags.EnumerateObject()
            .Single(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase)).Value.GetString()!)!;
        Assert.AreEqual("LOPATA", facts.Program); Assert.AreEqual("video-generation/minimax-h3", facts.Scenario);
        Assert.IsTrue(facts.GenerationSeconds >= prior.GenerationSeconds);
        Assert.IsTrue(facts.Models.Any(m => m.Id == "video-h3-upscale-model"));
        var operationTag = tags.EnumerateObject().Single(p => p.Name.Equals("LOPATA_OPERATION", StringComparison.OrdinalIgnoreCase)).Value.GetString();
        Assert.AreEqual(finished.Id, operationTag);
        var seconds = double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual(recorded.Items.Sum(i => i.Seconds), seconds, .1); Assert.AreEqual(seconds, result.Seconds, .1);
        var video = probe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.AreEqual("24/1", video.GetProperty("avg_frame_rate").GetString());
        Assert.IsTrue(video.GetProperty("width").GetInt32() * video.GetProperty("height").GetInt32()
            > current.Settings.Width * current.Settings.Height);
        File.WriteAllText(Path.Combine(evidence, "validated-before-install.json"), JsonSerializer.Serialize(new
            { operation.Id, SourceRun = prior.Id, RecoveredRun = finished.Id, seconds, OriginalSha256 = Convert.ToHexStringLower(baseline), facts }));

        // These are the first writes to user state, after all proof above. Recheck
        // the two compare-and-swap inputs immediately before the atomic save.
        AssertApplicationClosed();
        CollectionAssert.AreEqual(baseline, await FileHash(original));
        CollectionAssert.AreEqual(operationHash, await FileHash(operationPath));
        VideoPreparationArchive.Save(recovered, original);
        var installed = VideoPreparationArchive.Load(original, Path.Combine(evidence, "installed"));
        var installedRun = installed.Generator!.H3!.Runs.Single(r => r.Id == finished.Id);
        Assert.IsTrue(installedRun.Complete); Assert.AreEqual(1, installedRun.Results.Count);
        var completedJob = recorded with { ArchivePath = original, Run = installedRun,
            Settings = VideoShots.Copy(installed.Generator.H3.Settings), FinalizeOnly = true };
        var controller = new BackgroundOperationController(store); controller.Load();
        Assert.AreEqual(operation.Id, controller.State!.Id);
        var restored = operation with { Input = JsonSerializer.SerializeToElement(completedJob),
            ElapsedSeconds = installedRun.GenerationSeconds ?? operation.ElapsedSeconds, RequiresDecision = false };
        await controller.RunAsync(restored, _ => Task.FromResult(original), () => Task.CompletedTask, default);
        Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
        var saved = store.Load()!; Assert.AreEqual(operation.Id, saved.Id);
        Assert.AreEqual(BackgroundOperationPhase.Completed, saved.Phase);
        Assert.AreEqual(finished.Id, saved.Input.Deserialize<VideoH3Job>()!.Run.Id);
        Assert.IsFalse(controller.HasPending); Assert.IsNotNull(store.LoadResult(operation.Id));
        File.WriteAllText(Path.Combine(evidence, "installed.json"), JsonSerializer.Serialize(new
            { Archive = original, Operation = saved.Id, Run = finished.Id, saved.Phase, installedRun.GenerationSeconds,
                ArchiveSha256 = Convert.ToHexStringLower(await FileHash(original)), Result = installedRun.Results.Single() }));
    }

    private static async Task<byte[]> FileHash(string path)
    {
        await using var source = File.OpenRead(path); return await SHA256.HashDataAsync(source);
    }
    private static void AssertApplicationClosed()
    {
        var processes = Process.GetProcessesByName("AIHub");
        try { Assert.AreEqual(0, processes.Length, "Close the application before installing the completed recovery."); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
