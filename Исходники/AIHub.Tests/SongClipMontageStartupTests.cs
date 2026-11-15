using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services;
using AIHub.Services.SongClip;
using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SongClipMontageStartupTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task CompareAnySongPlannerAndBeatAnalysisStartup()
    {
        var baseline = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_STARTUP_SOURCE");
        var results = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_STARTUP_EVIDENCE");
        var python = Environment.GetEnvironmentVariable("LOPATA_MONTAGE_STARTUP_PYTHON");
        if (baseline is null || results is null || python is null)
        {
            Assert.Inconclusive("Explicit source, evidence and installed Python paths required.");
            return;
        }

        var fixture = new DirectoryInfo(Path.GetFullPath(baseline));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.FullName, "clip.mp4")), "Use a completed short fixture.");
        var attempt = JsonSerializer.Deserialize<SongClipAttempt>(File.ReadAllText(Path.Combine(fixture.FullName, "snapshot.json")))!;
        var store = new SongClipStore(fixture.Parent!.Parent!.Parent!.FullName);
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.FullName, "montage-analysis-request.json")))!.AsObject();
        var original = File.ReadAllBytes(Path.Combine(fixture.FullName, "montage-analysis.json"));

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var token = deadline.Token;
        await VideoRuntime.PrepareAsync(token);
        await RuntimeFileAudit.VerifyAsync("SongClip.H3Montage.Adapter", Path.Combine(AppContext.BaseDirectory, "Tools"),
            ApplicationBuildVersion.Value, H3MontageBinding.AdapterFiles(), token);

        var index = 0;
        foreach (var overlap in new[] { false, true, true, false })
        {
            await ApplicationBackgroundOperations.RetireModelsAsync();
            var job = Path.GetFullPath(Path.Combine(results, $"{++index}-{(overlap ? "overlap" : "sequential")}"));
            Assert.IsFalse(Directory.Exists(job), "Cases need their own analysis cache and evidence.");
            Directory.CreateDirectory(job);
            var request = source.DeepClone().AsObject();
            request["job"] = job;
            var start = DateTimeOffset.UtcNow;
            var timer = Stopwatch.StartNew();
            var plan = await H3MontageRunner.PreparePlanAsync(attempt, store, job, request, python,
                (_, stage) => File.AppendAllText(Path.Combine(job, "timeline.jsonl"), JsonSerializer.Serialize(new {
                    utc = DateTimeOffset.UtcNow, seconds = timer.Elapsed.TotalSeconds, stage
                }) + Environment.NewLine), token, overlap);
            timer.Stop();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(Path.Combine(job, "montage-analysis.json")));
            Assert.HasCount(2, plan.Shots);
            SongClipStore.Atomic(Path.Combine(job, "measurement.json"), new {
                overlap, start, end = DateTimeOffset.UtcNow, elapsed = timer.Elapsed.TotalSeconds,
                shots = plan.Shots.Length, model = plan.Assistants.Single().Name
            });
        }
    }
}
