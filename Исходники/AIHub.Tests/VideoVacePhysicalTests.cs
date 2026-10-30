using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoEditor;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

/// <summary>Explicit opt-in only: short real native sampling, never a user's project or prior run.</summary>
[TestClass, DoNotParallelize]
public sealed class VideoVacePhysicalTests
{
    [TestMethod, Timeout(1200000)]
    public async Task NativeStartTwoExtensionsAndOptionalPortableRecoveryWhenExplicitlyRequested()
    {
        var models = Environment.GetEnvironmentVariable("LOPATA_VACE_PHYSICAL_MODELS");
        var parent = Environment.GetEnvironmentVariable("LOPATA_VACE_PHYSICAL_DIRECTORY");
        if (Environment.GetEnvironmentVariable("LOPATA_VACE_PHYSICAL_GENERATE") != "1" || models is null || parent is null)
        { Assert.Inconclusive("Opt-in installed VACE generation test; no model is started by ordinary tests."); return; }
        Assert.IsTrue(Path.IsPathFullyQualified(models) && Path.IsPathFullyQualified(parent));
        var directory = Path.Combine(Path.GetFullPath(parent), "vace-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), enabled: false);
        var project = VideoPreparationFinishTests.ReadyProject();
        project.Title = "VACE native three-pass smoke"; project.CurrentStep = "finish";
        var scene = new VideoScenePlan { Name = "A continuous tabletop shot", Intent = "A red ball rolls slowly across a wooden table", Shots =
        [
            new() { Name = "Start", Start = "A small red ball starts rolling slowly right on a wooden table, warm studio light, continuous fixed close shot.",
                End = "The red ball is moving gently right.", Duration = 17d / 16 },
            new() { Name = "Continue", Start = "The same small red ball continues rolling gently right on the same wooden table, same warm light and fixed camera.",
                End = "The ball continues its unbroken movement.", Duration = 12d / 16 },
            new() { Name = "Finish", Start = "The same red ball continues the same slow movement to the right across the tabletop, continuous close shot, unchanged lighting.",
                End = "The ball slows down gently.", Duration = 12d / 16 }
        ] };
        project.Shots!.Scenes = [scene]; project.Shots.Translations.Clear(); project.Shots.SelectedTranslations.Clear();
        project.Shots.SelectedSceneId = scene.Id; project.Shots.SelectedShotId = scene.Shots[0].Id;
        var referenceSource = Environment.GetEnvironmentVariable("LOPATA_VACE_PHYSICAL_REFERENCE");
        string? referenceId = null, referenceSha = null;
        if (referenceSource is not null)
        {
            Assert.IsTrue(Path.IsPathFullyQualified(referenceSource));
            Assert.AreEqual(".png", Path.GetExtension(referenceSource).ToLowerInvariant(), "This optional physical fixture accepts PNG only.");
            _ = VideoReferenceFiles.Inspect(referenceSource);
            referenceSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(referenceSource)));
            var imported = await VideoReferenceFiles.ImportAsync(referenceSource, Path.Combine(directory, "reference"));
            Assert.AreEqual(".png", Path.GetExtension(imported.Path).ToLowerInvariant(), "The inspected raster must really be PNG.");
            Assert.AreNotEqual(Path.GetFullPath(referenceSource), Path.GetFullPath(imported.Path));
            Assert.AreEqual(referenceSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(imported.Path))));
            VideoReferences.Ensure(project).Choice = "import";
            project.References!.Images.Add(imported.Image); project.Attachments.Add(new(imported.Path, imported.Image.AttachmentRole));
            _ = VideoReferences.Bind(project, imported.Image.Id, "", "general", "Independent physical fixture reference");
            referenceId = imported.Image.Id;
            foreach (var shot in scene.Shots) shot.ReferenceIds = [referenceId];
        }
        project.Shots.Source = VideoShots.Hash(VideoShots.Source(project));
        VideoShots.Capture(project); project.Storyboard = null; VideoStoryboard.Ensure(project);
        if (referenceId is not null)
            foreach (var frame in project.Storyboard!.Frames) frame.ImageId = referenceId;
        VideoGeneratorPlan.SaveChoice(project, new(VideoVaceCatalog.ModelId, 320, 192, 16, 12345, "independent physical test"));
        var state = VideoVaceWorkspaceState.Ensure(project);
        state.Settings = new() { Width = 320, Height = 192, Frames = 17, ContextFrames = 5, Steps = 4,
            Seed = 12345, RetryCount = 1, MemoryMode = "auto", Crf = 19, OutputHeight = 0 };
        var archive = Path.Combine(directory, "smoke.lopataAIVid");
        var job = VideoVaceJob.Create(project, archive, models, 4);
        Assert.IsTrue(job.Items.All(i => i.Images.Length == (referenceId is null ? 0 : 1)));
        VideoPreparationArchive.Save(project, archive);
        var operationLog = Path.Combine(directory, "native.log"); var logGate = new object();
        void Log(string line) { lock (logGate) File.AppendAllText(operationLog, DateTimeOffset.UtcNow.ToString("O") + " " + line + "\n"); }
        var library = new ManagedModelLibraryStore(Path.Combine(directory, "library"));
        using var interrupted = new CancellationTokenSource();
        var recover = Environment.GetEnvironmentVariable("LOPATA_VACE_PHYSICAL_RECOVER") == "1";
        string? firstSha = null;
        try
        {
            await VideoVaceRunner.RunAsync(job, project, () =>
            {
                VideoPreparationArchive.Save(project, archive);
                if (recover && job.Run.Takes.Count == 1)
                {
                    firstSha = job.Run.Files.Single(f => f.RelativePath == job.Run.Takes[0].Video).Sha256;
                    interrupted.Cancel();
                }
                return Task.CompletedTask;
            }, Log, interrupted.Token, library: library);
        }
        catch (OperationCanceledException) when (recover && interrupted.IsCancellationRequested)
        {
            var id = job.Run.Id; Assert.HasCount(1, job.Run.Takes);
            var owned = Path.GetFullPath(VideoVaceRunner.DirectoryFor(id));
            var output = Path.GetFullPath(Path.Combine(owned, "output"));
            Assert.IsTrue(output.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Lopata.Updates.SafeUpdatePath.RejectLinks(output);
            // Move only this fresh test run's cache after its worker has been reaped; never touch a user's run.
            Directory.Move(output, output + "-before-recovery");
            project = VideoPreparationArchive.Load(archive, Path.Combine(directory, "reopened"));
            job = VideoVaceJob.Create(project, archive, models, 4); Assert.AreEqual(id, job.Run.Id);
            await VideoVaceRunner.RunAsync(job, project, () => { VideoPreparationArchive.Save(project, archive); return Task.CompletedTask; },
                Log, CancellationToken.None, library: library);
            Assert.AreEqual(firstSha, job.Run.Files.Single(f => f.RelativePath == job.Run.Takes[0].Video).Sha256);
        }
        Assert.IsTrue(job.Run.Complete); Assert.HasCount(3, job.Run.Takes); Assert.HasCount(1, job.Run.Results);
        CollectionAssert.AreEqual(new[] { 17, 17, 17 }, job.Run.Takes.Select(t => t.Frames).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 5, 5 }, job.Run.Takes.Select(t => t.PrefixFrames).ToArray());
        CollectionAssert.AreEqual(new[] { "", job.Run.Takes[0].Id, job.Run.Takes[1].Id }, job.Run.Takes.Select(t => t.Parent).ToArray());
        Assert.AreEqual(41 / 16d, job.Run.Results[0].Seconds); Assert.IsTrue(job.Run.GenerationSeconds > 0);
        var result = project.Attachments.Single(a => a.Role == job.Run.Results[0].VideoRole).Path;
        using var probe = await VideoRuntime.ProbeAsync(result, CancellationToken.None);
        var stream = probe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.AreEqual("16/1", stream.GetProperty("avg_frame_rate").GetString()); Assert.AreEqual("41", stream.GetProperty("nb_frames").GetString());
        var tag = probe.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
            .Single(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
        var facts = JsonSerializer.Deserialize<OutputProvenance>(tag.Value.GetString()!)!;
        Assert.AreEqual("LOPATA", facts.Program); Assert.AreEqual("video-generation/wan-vace-1.3b", facts.Scenario);
        Assert.IsTrue(facts.GenerationSeconds > 0); Assert.IsTrue(facts.Models.Any(m => m.Id.StartsWith(VideoVaceCatalog.ComponentId + "/", StringComparison.Ordinal)));
        Assert.IsFalse(facts.Models.Any(m => m.Id == "video-h3-models" || m.Id == "video-h3-source-timeline"));
        foreach (var take in job.Run.Takes.Skip(1))
        {
            var role = job.Run.Files.Single(f => f.RelativePath == Path.ChangeExtension(take.Video, ".lineage.json").Replace('\\', '/')).Role;
            using var lineage = JsonDocument.Parse(File.ReadAllText(project.Attachments.Single(a => a.Role == role).Path));
            Assert.AreEqual(64, lineage.RootElement.GetProperty("PrefixRgbSha256").GetString()!.Length);
            Assert.AreEqual(64, lineage.RootElement.GetProperty("ParentVideoSha256").GetString()!.Length);
        }
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(job.Run));
        if (referenceSource is not null)
            Assert.AreEqual(referenceSha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(referenceSource))), "The source PNG is read-only test input.");
        File.WriteAllText(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            { directory, archive, result, frames = 41, fps = 16, job.Run.GenerationSeconds, recovered = recover,
                reference_id = referenceId, reference_sha256 = referenceSha }));
        TestContext?.WriteLine("VACE evidence: " + directory);
    }
    public TestContext? TestContext { get; set; }
}
