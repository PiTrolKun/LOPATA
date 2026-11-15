using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

public sealed partial class SongClipMontageContextReplayTests
{
    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task CompareInstalledSongPlanners()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_COMPARE_PLANNERS_SOURCE");
        var evidence = Environment.GetEnvironmentVariable("LOPATA_COMPARE_PLANNERS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(evidence))
        {
            Assert.Inconclusive("Explicit source and evidence paths required.");
            return;
        }

        var attempt = Read<SongClipAttempt>(source, "snapshot.json");
        var analysis = Read<SongClipMontageAnalysis>(source, "montage-analysis.json");
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = ExistingConsentOnly;

        var models = new[] { "executor", ManagedModelCatalog.LetItOutArtifactId };
        var isFixed = int.TryParse(Environment.GetEnvironmentVariable("LOPATA_COMPARE_PLANNERS_CONTEXT"), out var context);
        var cases = int.TryParse(Environment.GetEnvironmentVariable("LOPATA_COMPARE_PLANNERS_REPEATS"), out var repeats) ? repeats : 1;
        var compare = Environment.GetEnvironmentVariable("LOPATA_COMPARE_PLANNERS_MODEL");
        if (!string.IsNullOrWhiteSpace(compare)) models = models.Where(id => id == compare || compare == "gemma" && id != "executor").ToArray();
        var failures = new List<string>();
        try
        {
            using var audit = ComponentAudit.CaptureOperation();
            for (var run = 1; run <= cases; run++)
            foreach (var id in models)
            {
                var folder = Path.GetFullPath(Path.Combine(evidence, id + "-" + run));
                Directory.CreateDirectory(folder);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
                var watch = Stopwatch.StartNew();
                int requests = 0, repairs = 0, smaller = 0;
                var peaks = new Dictionary<int, long>();
                double warmSeconds = 0;
                string? error = null;
                string? model = null;
                int? sceneCount = null;
                try
                {
                    await using var session = await SongClipPlannerModel.OpenAsync(attempt, timeout.Token, id, isFixed ? context : null);
                    await session.WarmAsync(timeout.Token);
                    warmSeconds = watch.Elapsed.TotalSeconds;
                    var key = SongClipMontageProtocol.Hash(new { inputs = SongClipMontageProtocol.InputHash(attempt, analysis), id, run });
                    var plan = await SongClipMontagePlanning.EnsureCoreAsync(attempt, folder, analysis, key,
                        async (system, input, schema, token) =>
                        {
                            var requestWatch = Stopwatch.StartNew();
                            using var metadata = JsonDocument.Parse(input);
                            var slots = metadata.RootElement.TryGetProperty("slots", out var values) ? values.GetArrayLength() : 0;
                            var reserve = SongClipPlannerRecovery.Reserve(input);
                            requests++;
                            try { return await session.GenerateAsync(system, input, schema, reserve, token); }
                            finally
                            {
                                var runtime = session.RuntimeInfo;
                                if (runtime is not null)
                                {
                                    try
                                    {
                                        using var process = Process.GetProcessById(runtime.Pid);
                                        peaks[runtime.Pid] = Math.Max(peaks.GetValueOrDefault(runtime.Pid), process.PeakWorkingSet64);
                                    }
                                    catch (Exception unavailable) when (unavailable is ArgumentException or InvalidOperationException) { }
                                }
                                File.AppendAllText(Path.Combine(folder, "requests.jsonl"), JsonSerializer.Serialize(new
                                {
                                    requests, slots, reserve, seconds = requestWatch.Elapsed.TotalSeconds, runtime
                                }) + Environment.NewLine);
                            }
                        }, (_, stage) =>
                        {
                            if (stage == "SongClip.PlanRepairing") repairs++;
                            if (stage == "SongClip.PlanSmallerBatch") smaller++;
                            File.AppendAllText(Path.Combine(folder, "stages.jsonl"),
                                JsonSerializer.Serialize(new { seconds = watch.Elapsed.TotalSeconds, stage }) + Environment.NewLine);
                        }, timeout.Token, prepareStory: true, growBatches: !isFixed);
                    sceneCount = plan.Shots.Length;
                    model = plan.Assistants.Single().Name;
                    _ = SongClipMontageProtocol.Validate(plan, attempt, analysis);
                    Assert.HasCount(analysis.Slots.Length, plan.Shots);
                }
                catch (Exception failure)
                {
                    failures.Add(id + ": " + failure.Message);
                    error = failure.ToString();
                }
                SongClipStore.Atomic(Path.Combine(folder, "metrics.json"), new
                {
                    planner = id, model, sceneCount, expected = analysis.Slots.Length, requests, repairs, smaller, warmSeconds,
                    elapsedSeconds = watch.Elapsed.TotalSeconds, peakWorkerRamBytes = peaks.Values.DefaultIfEmpty().Max(), error
                });
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousGate; }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
