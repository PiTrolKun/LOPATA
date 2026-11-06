using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

/// <summary>Explicit physical art check with genuine installed-model acknowledgements and no downloads.</summary>
[TestClass, DoNotParallelize]
public sealed class LivingArcadeSceneryNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ActualArtistDrawsOneChapterWithoutRegeneratingPreparedCombat()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_SCENERY_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output))
        { Assert.Inconclusive("Opt in with LOPATA_ARCADE_SCENERY_NATIVE_DIR and genuine current model acknowledgements."); return; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var ids = new[] { LivingArcadeModels.MainArtifactId, LivingArcadeModels.ArtistArtifactId };
        var receipts = licenses.ReadReceipts();
        var pending = licenses.Entries.Where(entry => ids.Contains(entry.Id)
            && !receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms)).Select(entry => entry.Id).ToArray();
        Assert.AreEqual(0, pending.Length, "Current genuine application acknowledgement is required: " + string.Join(", ", pending));
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var previousBackground = ApplicationBackgroundOperations.Current;
        Assert.IsNull(previousBackground, "An isolated native check cannot replace the live application's controller.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var generation = new LivingArcadeGeneration(new(new UserProfileStore(), new IpLocationService()));
        var calls = new List<LivingArcadeInferenceMetric>();
        var traceIndex = 0;
        generation.InferenceCompleted += calls.Add;
        generation.InferenceTrace += trace => File.WriteAllText(Path.Combine(output, $"scenery-inference-{++traceIndex:D2}.json"),
            JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
        ApplicationBackgroundOperations.Current = new(new BackgroundOperationStore(Path.Combine(output, "background", "state.json")));
        ComponentLicenseGate.ConfirmAsync = (requested, token) => licenses.EnsureAsync(
            ComponentLicenseService.ExpandSelection(requested, licenses.Entries), _ => Task.FromResult(false), token);
        var clock = Stopwatch.StartNew();
        try
        {
            await EngineSetService.Shared.InitializeAsync(deadline.Token);
            var ready = await LivingArcadeModels.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 4,
                download: false, new Progress<LivingArcadePreparationProgress>(), deadline.Token);
            Assert.IsTrue(ready.Ready, ready.StatusKey);
            var seed = Enumerable.Range(734_169, 64).First(value => LivingArcadeSceneryArtPolicy.ShouldGenerate(value, 0));
            var store = new LivingArcadeRunStore(Path.Combine(output, "isolated-runs-" + Guid.NewGuid().ToString("N")));
            var run = store.Create("Physical original scenery", seed);
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(seed) with { Number = 2, Authored = false });
            run = store.Update(run.Id, value =>
            {
                value.FirstEventsPrepared = true;
                value.Draft = new() { Number = 3, Concept = "Already prepared future concept", NewMechanic = "Timed waves" };
            });
            var before = run.Levels.ToDictionary(pair => pair.Key, pair => LivingArcadeSceneryArtTests.WithoutArt(pair.Value));
            var progress = LivingArcadeSceneryArtTests.Progress(run);
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            var reopened = store.Load(run.Id)!;
            var result = store.GetChapterScenery(run.Id, 1);
            await File.WriteAllTextAsync(Path.Combine(output, "scenery-native-result.json"), JsonSerializer.Serialize(new
            {
                elapsedSeconds = clock.Elapsed.TotalSeconds, calls, result,
                progressUnchanged = progress == LivingArcadeSceneryArtTests.Progress(reopened),
                modelReceiptsWereNotModified = true, weightsDownloaded = false,
                generatedByActualModel = result?.Outcome == "generated"
            }, LivingArcadeLevelPolicy.JsonOptions), deadline.Token);
            Assert.IsNotNull(result);
            Assert.AreEqual("generated", result.Outcome, "The genuine artist must supply accepted art; a fallback is not a successful model test.");
            Assert.IsNotNull(result.Artwork);
            await File.WriteAllTextAsync(Path.Combine(output, "chapter-scenery.json"),
                JsonSerializer.Serialize(result.Artwork, LivingArcadeLevelPolicy.JsonOptions), deadline.Token);
            await LivingArcadeGeneratedBackgroundRenderTests.RenderGenuineSceneryAsync(result.Artwork, output);
            Assert.AreEqual(progress, LivingArcadeSceneryArtTests.Progress(reopened));
            foreach (var (number, level) in reopened.Levels)
            {
                Assert.AreEqual(before[number], LivingArcadeSceneryArtTests.WithoutArt(level));
                Assert.AreEqual(JsonSerializer.Serialize(result.Artwork), JsonSerializer.Serialize(level.Scenery));
            }
            Assert.AreEqual(1, calls.Count, "Prepared levels and draft must leave only one optional scenery inference.");
            Assert.AreEqual(LivingArcadeModels.ArtistArtifactId, calls[0].ModelId);
            Assert.AreEqual("Arcade.Ai.Drawing", calls[0].Stage);
            Assert.AreEqual("Qwen/Qwen2.5-Coder-3B-Instruct-GGUF", result.Artwork.Credits.Single().ModelId);
            var beforeReopen = calls.Count;
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            Assert.AreEqual(beforeReopen, calls.Count, "A terminal accepted result is reused without another model call.");
        }
        finally
        {
            try { await generation.RetireAsync(); }
            finally { ComponentLicenseGate.ConfirmAsync = previousGate; ApplicationBackgroundOperations.Current = previousBackground; }
        }
    }
}
