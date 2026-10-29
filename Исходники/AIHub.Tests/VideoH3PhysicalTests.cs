using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3PhysicalTests
{
    [TestMethod, Timeout(3600000)]
    public async Task PinnedNativeRuntimeAndThreePassesWhenExplicitlyRequested()
    {
        var models = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_MODELS");
        var directory = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_DIRECTORY");
        if (models is null || directory is null) { Assert.Inconclusive("Opt-in installed H3 test."); return; }
        Directory.CreateDirectory(directory);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), enabled: false);
        var project = VideoPreparationFinishTests.ReadyProject(); project.Title = "H3 native smoke test";
        project.Storyboard!.Frames = project.Storyboard.Frames.Take(1).ToList(); project.CurrentStep = "finish";
        var h3 = VideoH3WorkspaceState.Ensure(project);
        h3.Settings = new() { RunMode = "automatic", Width = 416, Height = 256, Steps = 8, ClipSeconds = 5, TargetSeconds = 12, Seed = 12345 };
        h3.Plans[project.Storyboard.Frames[0].ShotId] = new()
        {
            Prompt = "A fixed close shot of a small brass clock on a wooden desk, warm studio light, gentle dust in the air, steady camera, a continuous shot.",
            ReferenceIds = []
        };
        // Explicit opt-in replay copies inputs into an independent project. It
        // never opens or overwrites the user's archive or resumes their run.
        var replay = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_REQUEST");
        if (replay is not null)
        {
            using var source = JsonDocument.Parse(File.ReadAllText(replay));
            h3.Settings = source.RootElement.GetProperty("Settings").Deserialize<VideoH3Settings>()!;
            h3.Settings.Steps = int.Parse(Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_STEPS") ?? "3");
            h3.Settings.RunMode = "review"; h3.Settings.TargetSeconds = 0;
            var reserve = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_RESERVE");
            if (reserve is not null) h3.Settings.VramReserveGb = double.Parse(reserve, System.Globalization.CultureInfo.InvariantCulture);
            var memoryMode = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_MEMORY");
            if (memoryMode is not null) h3.Settings.MemoryMode = memoryMode;
            var firstItem = source.RootElement.GetProperty("Items")[0];
            var edit = h3.Plans[project.Storyboard.Frames[0].ShotId];
            edit.Prompt = firstItem.GetProperty("Prompt").GetString();
            edit.Seconds = firstItem.GetProperty("Seconds").GetDouble();
            foreach (var input in firstItem.GetProperty("Images").EnumerateArray())
            {
                var imported = await VideoReferenceFiles.ImportAsync(input.GetString()!, Path.Combine(directory, "replay-" + project.Id));
                project.References!.Images.Add(imported.Image); project.Attachments.Add(new(imported.Path, imported.Image.AttachmentRole));
                edit.ReferenceIds!.Add(imported.Image.Id);
            }
        }
        var image = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_IMAGE");
        if (image is not null)
        {
            var imported = await VideoReferenceFiles.ImportAsync(image, Path.Combine(directory, "image-" + project.Id));
            project.References!.Images.Add(imported.Image); project.Attachments.Add(new(imported.Path, imported.Image.AttachmentRole));
            h3.Plans[project.Storyboard.Frames[0].ShotId].ReferenceIds = [imported.Image.Id];
            h3.Settings.RunMode = "review"; h3.Settings.TargetSeconds = 5;
        }
        var existing = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_ARCHIVE");
        var archive = existing ?? Path.Combine(directory, "smoke-" + project.Id + ".lopataAIVid");
        if (existing is null) VideoPreparationArchive.Save(project, archive);
        else
        {
            project = VideoPreparationArchive.Load(existing, Path.Combine(directory, "existing-" + Guid.NewGuid().ToString("N")));
            var prior = project.Generator!.H3!.Runs.Last(); prior.Complete = false; prior.Results.Clear();
            archive = Path.Combine(directory, "assembly-" + Guid.NewGuid().ToString("N") + ".lopataAIVid");
            VideoPreparationArchive.Save(project, archive);
        }
        var upscaling = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_UPSCALE") == "1";
        var previous = project.Generator?.H3?.Runs.LastOrDefault();
        if (upscaling)
        {
            Assert.IsNotNull(previous); Assert.AreEqual(3, previous.Takes.Count);
            var settings = project.Generator!.H3!.Settings;
            settings.Upscale = true; settings.UpscaleMegapixels = .25; settings.RefineSteps = 2;
            settings.WindowSeconds = 1; settings.OverlapSeconds = .25;
        }
        var job = VideoH3Job.Create(project, archive, models, 8);
        if (upscaling)
        {
            // The independent refinement fixture reuses already verified real
            // base takes, preserving their native names, pairing and lineage.
            foreach (var file in previous!.Files.Where(f => previous.Takes.Any(t => Path.GetFileNameWithoutExtension(f.RelativePath).StartsWith(Path.GetFileNameWithoutExtension(t.Video), StringComparison.Ordinal))))
            {
                var role = "h3-run/" + job.Run.Id + "/" + Guid.NewGuid().ToString("N");
                project.Attachments.Add(new(project.Attachments.Single(a => a.Role == file.Role).Path, role));
                job.Run.Files.Add(file with { Role = role });
            }
            job.Run.Takes.AddRange(previous.Takes);
        }
        var probe = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_GENERATE") != "1";
        var log = Path.Combine(directory, "native.log");
        var logGate = new object();
        void Log(string line) { lock (logGate) File.AppendAllText(log, DateTimeOffset.UtcNow.ToString("O") + " " + line + "\n"); }
        using var interrupted = new CancellationTokenSource();
        var recover = !probe && Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_RECOVER") == "1";
        try
        {
            await VideoH3Runner.RunAsync(job, project, () =>
                { VideoPreparationArchive.Save(project, archive); if (recover && job.Run.Takes.Count == 1) interrupted.Cancel(); return Task.CompletedTask; },
                Log, interrupted.Token, probeOnly: probe, library: new ManagedModelLibraryStore(Path.Combine(directory, "library")));
        }
        catch (OperationCanceledException) when (recover && interrupted.IsCancellationRequested)
        {
            var originalId = job.Run.Id;
            var output = Path.GetFullPath(Path.Combine(VideoH3Runner.DirectoryFor(originalId), "output"));
            // Only this test's owned output cache is moved, after its child has exited.
            Assert.IsTrue(output.StartsWith(VideoH3Runner.DirectoryFor(originalId) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Directory.Move(output, output + "-before-recovery");
            project = VideoPreparationArchive.Load(archive, Path.Combine(directory, "reopened-" + project.Id));
            job = VideoH3Job.Create(project, archive, models, 8); Assert.AreEqual(originalId, job.Run.Id);
            await VideoH3Runner.RunAsync(job, project, () => { VideoPreparationArchive.Save(project, archive); return Task.CompletedTask; },
                Log, CancellationToken.None, library: new ManagedModelLibraryStore(Path.Combine(directory, "library")));
        }
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(job.Run));
        if (!probe)
        {
            if (replay is not null)
            {
                Assert.AreEqual(1, job.Run.Takes.Count); Assert.AreEqual(1, job.Run.Results.Count);
                Assert.AreEqual("fragment", job.Run.Results[0].Kind); return;
            }
            if (image is not null)
            {
                Assert.IsTrue(job.Run.Complete); Assert.AreEqual(1, job.Run.Takes.Count);
                Assert.AreEqual(1, job.Run.Results.Count); Assert.AreEqual("fragment", job.Run.Results[0].Kind);
                return;
            }
            Assert.IsTrue(job.Run.Complete); Assert.AreEqual(3, job.Run.Takes.Count);
            Assert.AreEqual("", job.Run.Takes[0].Parent); Assert.AreNotEqual("", job.Run.Takes[1].Parent); Assert.AreNotEqual("", job.Run.Takes[2].Parent);
            Assert.AreEqual(1, job.Run.Results.Count); Assert.AreEqual("whole", job.Run.Results[0].Kind);
            var seconds = job.Run.Results[0].Seconds;
            var estimate = job.Items.Sum(i => i.Seconds);
            Assert.IsTrue(seconds >= estimate - .05 && seconds <= estimate + VideoH3WorkspaceState.RawFrames(job.Settings.ClipSeconds) / 24d);
        }
    }
}
