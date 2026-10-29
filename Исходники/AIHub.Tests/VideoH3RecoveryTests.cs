using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoH3RecoveryTests
{
    [TestMethod]
    public async Task ScheduledWaitCancelsWithoutExecutingAndJobRetainsUtcDeadline()
    {
        var now = DateTimeOffset.UtcNow; using var cancel = new CancellationTokenSource(); var observed = false;
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => VideoH3Schedule.WaitAsync(now.AddHours(1), remaining =>
            { observed = remaining > TimeSpan.Zero; cancel.Cancel(); }, cancel.Token));
        Assert.IsTrue(observed);
        Assert.AreEqual(TimeSpan.Zero, VideoH3Schedule.Remaining(now.AddMinutes(-1), now));
        var project = VideoPreparationFinishTests.ReadyProject();
        var job = VideoH3Job.Create(project, "fixture.lopataAIVid", "models", 4) with { ScheduledUtc = now.AddHours(1) };
        var recovered = JsonSerializer.Deserialize<VideoH3Job>(JsonSerializer.Serialize(job))!;
        Assert.AreEqual(job.ScheduledUtc, recovered.ScheduledUtc); Assert.AreEqual(job.Run.Id, recovered.Run.Id);
    }
    [TestMethod, Timeout(600000)]
    public async Task InstalledTwoPlanRunSavesContinuityDurationAndScaledVideo()
    {
        var directory = Environment.GetEnvironmentVariable("LOPATA_H3_FEEDBACK_PHYSICAL");
        var models = Environment.GetEnvironmentVariable("LOPATA_H3_PHYSICAL_MODELS");
        if (directory is null || models is null) { Assert.Inconclusive("Opt-in independent feedback smoke test."); return; }
        Directory.CreateDirectory(directory);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(directory, "audit"), enabled: false);
        var project = VideoPreparationFinishTests.ReadyProject();
        var state = VideoH3WorkspaceState.Ensure(project);
        state.Settings = new() { Width = 256, Height = 256, Aspect = "1:1", Steps = 2, ClipSeconds = 5, TargetSeconds = 5, OutputHeight = 720, Seed = 151 };
        foreach (var plan in VideoH3WorkspacePlans.From(project)) state.Plans[plan.Shot.Id] = new()
            { ReferenceIds = [], Prompt = "A small brass clock on a wooden desk in warm light, steady close camera, the hand moves slowly." };
        var archive = Path.Combine(directory, "independent.lopataAIVid");
        VideoPreparationArchive.Save(project, archive);
        var job = VideoH3Job.Create(project, archive, models, 4);
        var logGate = new object();
        void Log(string line) { lock (logGate) File.AppendAllText(Path.Combine(directory, "runner.log"), DateTimeOffset.UtcNow.ToString("O") + " " + line + "\n"); }
        await VideoH3Runner.RunAsync(job, project, () => { VideoPreparationArchive.Save(project, archive); return Task.CompletedTask; },
            Log, default,
            library: new ManagedModelLibraryStore(Path.Combine(directory, "library")));
        Assert.IsTrue(job.Run.Complete); Assert.AreEqual(2, job.Run.Takes.Count);
        Assert.AreEqual(job.Run.Takes[0].Video, job.Run.Takes[1].Parent); Assert.IsTrue(job.Run.GenerationSeconds > 0);
        var result = job.Run.Results.Single();
        var estimate = job.Items.Sum(i => i.Seconds);
        Assert.IsTrue(result.Seconds >= estimate - .05 && result.Seconds <= estimate + VideoH3WorkspaceState.RawFrames(job.Settings.ClipSeconds) / 24d);
        using var probe = await AIHub.Services.VideoEditor.VideoRuntime.ProbeAsync(project.Attachments.Single(a => a.Role == result.VideoRole).Path, default);
        var tags = probe.RootElement.GetProperty("format").GetProperty("tags");
        var facts = tags.EnumerateObject().Single(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        using var metadata = JsonDocument.Parse(facts); Assert.IsTrue(metadata.RootElement.GetProperty("GenerationSeconds").GetDouble() > 0);
        var stream = probe.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.AreEqual(720, stream.GetProperty("width").GetInt32()); Assert.AreEqual(720, stream.GetProperty("height").GetInt32());
        var reopened = VideoPreparationArchive.Load(archive, Path.Combine(directory, "reopened"));
        Assert.IsTrue(reopened.Generator!.H3!.Runs.Single().GenerationSeconds > 0);
    }
    [TestMethod]
    public async Task RetryContinuesSameRunAfterConfirmedCheckpoint()
    {
        var run = new VideoH3Run { GenerationSeconds = 0 };
        var attempts = 0; var commits = 0;
        await VideoH3Recovery.RunAsync(run, 1, elapsed =>
        {
            attempts++;
            if (attempts == 1)
            {
                run.Takes.Add(new("saved", "item", "scene", "first.mp4", 1, 5, ""));
                throw new VideoH3WorkerCrashException("native crash");
            }
            Assert.AreEqual("saved", run.Takes.Single().Id);
            Assert.IsTrue(elapsed() >= 0); run.Complete = true; return Task.CompletedTask;
        }, () => { commits++; return Task.CompletedTask; }, _ => { }, default);
        Assert.AreEqual(2, attempts); Assert.AreEqual(2, commits);
        Assert.IsTrue(run.Complete); Assert.IsTrue(run.GenerationSeconds >= 0);
    }

    [TestMethod]
    public async Task ExhaustedBudgetAndUnknownFailureNeverLoop()
    {
        var attempts = 0; var run = new VideoH3Run { GenerationSeconds = 0, RetryAttempts = 1 };
        var exhausted = await Assert.ThrowsExactlyAsync<AIHub.Models.BackgroundOperationWaitingException>(() => VideoH3Recovery.RunAsync(run, 1,
            _ => { attempts++; throw new VideoH3WorkerCrashException("native crash"); }, () => Task.CompletedTask, _ => { }, default));
        Assert.AreEqual("VideoPrep.H3RetriesExhausted", exhausted.Message);
        Assert.AreEqual(1, attempts);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VideoH3Recovery.RunAsync(new(), 5,
            _ => throw new InvalidDataException("audit failure"), () => Task.CompletedTask, _ => { }, default));
    }

    [TestMethod]
    public async Task CancellationIsNeverRetriedAndUnknownLegacyTimeStaysUnknown()
    {
        var run = new VideoH3Run(); using var cancel = new CancellationTokenSource(); var attempts = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => VideoH3Recovery.RunAsync(run, 5,
            _ => { attempts++; cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }, () => Task.CompletedTask, _ => { }, cancel.Token));
        Assert.AreEqual(1, attempts); Assert.IsNull(run.GenerationSeconds);
    }

    [TestMethod]
    public void QualityScaleKeepsEveryLegacyCrfAndHasMonotoneDirection()
    {
        Assert.AreEqual(85, VideoH3Quality.ToPercent(19));
        foreach (var crf in new[] { 0, 16, 18, 19, 20, 23, 28, 35, 51 })
            Assert.AreEqual(crf, VideoH3Quality.ToCrf(VideoH3Quality.ToPercent(crf)));
        for (var quality = 1; quality <= 100; quality++)
            Assert.IsTrue(VideoH3Quality.ToCrf(quality) <= VideoH3Quality.ToCrf(quality - 1));
    }
}
