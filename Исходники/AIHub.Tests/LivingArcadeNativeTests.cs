using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

/// <summary>Explicit physical integration. It never invents license acceptance or a generated model result.</summary>
[TestClass, DoNotParallelize]
public sealed class LivingArcadeNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task PinnedModelsPrepareWarmAndGenerateFirstEventsNextLevelAndFutureDraft()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt in with LOPATA_ARCADE_NATIVE_DIR after genuine component acknowledgement in the application."); return; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var ids = new[] { LivingArcadeModels.MainArtifactId, LivingArcadeModels.ArtistArtifactId };
        var receipts = service.ReadReceipts();
        var pending = service.Entries.Where(e => ids.Contains(e.Id) && !receipts.Any(r => r.Id == e.Id && r.Terms == e.Terms))
            .Select(e => e.Id).ToArray();
        if (pending.Length != 0)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "consent-required.json"), JsonSerializer.Serialize(new {
                pending, executed = false, weightsDownloadedByThisTest = false,
                reason = "Genuine current application acknowledgements are required. No receipt was added or modified."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Inconclusive("Use the application's standard component acknowledgement before the physical test: " + string.Join(", ", pending));
            return;
        }
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var previousBackground = ApplicationBackgroundOperations.Current;
        Assert.IsNull(previousBackground, "The isolated physical test cannot replace a live controller.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var stages = new List<object>();
        var calls = new List<LivingArcadeInferenceMetric>();
        var times = new Dictionary<string, double>();
        LivingArcadeRun? actualRun = null;
        using var generation = new LivingArcadeGeneration(new(new UserProfileStore(), new IpLocationService()));
        generation.StatusChanged += status => {
            stages.Add(new { status, utc = DateTimeOffset.UtcNow, runtime = generation.RuntimeSession });
            TestContext.WriteLine(status);
        };
        generation.InferenceCompleted += calls.Add;
        var traceIndex = 0;
        generation.InferenceTrace += trace => {
            var number = Interlocked.Increment(ref traceIndex);
            File.WriteAllText(Path.Combine(output, $"inference-{number:D2}.json"), JsonSerializer.Serialize(trace,
                new JsonSerializerOptions { WriteIndented = true }));
        };
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(output, "background", "state.json")));
        // Same service as the product, reusing real receipts. A new/pending licence is declined, never fabricated.
        ComponentLicenseGate.ConfirmAsync = (requested, ct) => service.EnsureAsync(
            ComponentLicenseService.ExpandSelection(requested, service.Entries), _ => Task.FromResult(false), ct);
        var clock = Stopwatch.StartNew();
        try
        {
            await EngineSetService.Shared.InitializeAsync(timeout.Token);
            var preparation = Stopwatch.StartNew();
            var ready = await LivingArcadeModels.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 4,
                Environment.GetEnvironmentVariable("LOPATA_ARCADE_NATIVE_DOWNLOAD") == "1",
                new Inline<LivingArcadePreparationProgress>(p => TestContext.WriteLine(
                    p.StageKey + (p.TotalBytes > 0 ? $" {p.DownloadedBytes}/{p.TotalBytes}" : ""))), timeout.Token);
            times["preparationSeconds"] = preparation.Elapsed.TotalSeconds;
            Assert.IsTrue(ready.Ready, ready.StatusKey);
            var paths = ready.Paths!;
            await Measure("warmSeconds", () => generation.WarmAsync(paths, "ru", timeout.Token));
            var store = new LivingArcadeRunStore(Path.Combine(output, "isolated-runs"));
            actualRun = store.Create("Physical model integration", 136_877);
            var original = JsonSerializer.Serialize(actualRun.Levels[1], LivingArcadeLevelPolicy.JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(output, "authored-first-before.json"), original, timeout.Token);
            await Measure("firstEventsSeconds", () => generation.PrepareFirstEventsAsync(store, actualRun.Id, paths, "ru", timeout.Token));
            actualRun = store.Load(actualRun.Id)!;
            Assert.IsTrue(actualRun.FirstEventsPrepared);
            Assert.IsTrue(actualRun.Levels[1].Authored);
            Assert.IsTrue(actualRun.Levels[1].MiniEvents.Count >= 6);
            await Measure("aheadSeconds", () => generation.PrepareAheadAsync(store, actualRun.Id, paths, "ru", timeout.Token));
            actualRun = store.Load(actualRun.Id)!;
            Assert.IsTrue(actualRun.Levels.ContainsKey(2));
            Assert.AreEqual(3, actualRun.Draft?.Number);
            Assert.IsFalse(actualRun.Levels[2].Authored);
            Assert.AreEqual(4, actualRun.Levels[2].Credits.Count);
            LivingArcadeLevelPolicy.Validate(actualRun.Levels[2]);
            var probe = await Task.Run(() => LivingArcadeLevelPolicy.Probe(actualRun.Levels[2], actualRun.EntryState, timeout.Token), timeout.Token);
            Assert.IsTrue(probe.Accepted, probe.RejectionKey);
            Assert.IsTrue(calls.Any(c => c.ModelId == LivingArcadeModels.ArtistArtifactId));
            Assert.IsTrue(calls.Any(c => c.Stage == "Arcade.Ai.Reviewing"));
            await File.WriteAllTextAsync(Path.Combine(output, "accepted-next-level.json"),
                JsonSerializer.Serialize(actualRun.Levels[2], LivingArcadeLevelPolicy.JsonOptions), timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "probe.json"),
                JsonSerializer.Serialize(probe, LivingArcadeLevelPolicy.JsonOptions), timeout.Token);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new {
                type = error.GetType().FullName, message = error.Message,
                validationDetail = error.Data["ValidationDetail"] as string,
                inner = error.InnerException?.Message
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            try { await generation.RetireAsync(); }
            finally
            {
                ApplicationBackgroundOperations.Current = previousBackground;
                ComponentLicenseGate.ConfirmAsync = previousGate;
                await File.WriteAllTextAsync(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new {
                    producer = "LOPATA", scenario = "LivingArcade native integration", createdUtc = DateTimeOffset.UtcNow,
                    totalSeconds = clock.Elapsed.TotalSeconds, times, stages, calls,
                    runId = actualRun?.Id, modelExecutionRetired = generation.RuntimeSession is null
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Assert.IsNull(generation.RuntimeSession, "The native owned worker must really retire.");
        }
        async Task Measure(string name, Func<Task> work)
        { var timer = Stopwatch.StartNew(); try { await work(); } finally { times[name] = timer.Elapsed.TotalSeconds; } }
    }

    private sealed class Inline<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
}
