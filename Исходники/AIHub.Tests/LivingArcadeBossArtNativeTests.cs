using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeBossArtNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ActualArtistCreatesAndReusesDetailedBossWithoutRewritingPreparedCombat()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_BOSS_ART_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output))
        { Assert.Inconclusive("Opt in with LOPATA_ARCADE_BOSS_ART_NATIVE_DIR and genuine installed-model acknowledgements."); return; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var ids = new[] { LivingArcadeModels.MainArtifactId, LivingArcadeModels.ArtistArtifactId };
        var receipts = licenses.ReadReceipts();
        Assert.IsFalse(licenses.Entries.Any(entry => ids.Contains(entry.Id)
            && !receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms)),
            "Genuine current model acknowledgement is required; this test must not create it.");
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var previousBackground = ApplicationBackgroundOperations.Current;
        Assert.IsNull(previousBackground);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var generation = new LivingArcadeGeneration(new(new UserProfileStore(), new IpLocationService()));
        var calls = new List<LivingArcadeInferenceMetric>();
        var traceIndex = 0;
        generation.InferenceCompleted += calls.Add;
        generation.InferenceTrace += trace => File.WriteAllText(Path.Combine(output, $"boss-inference-{++traceIndex:D2}.json"),
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
            const int seed = 734169;
            var store = new LivingArcadeRunStore(Path.Combine(output, "isolated-runs-" + Guid.NewGuid().ToString("N")));
            var run = store.Create("Physical detailed boss art", seed);
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(seed) with { Number = 2, Authored = false });
            store.Update(run.Id, value => { value.CurrentLevel = 2; value.FirstEventsPrepared = true; });
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(seed) with
            {
                Number = 3, Authored = false, TitleEn = "Mechanical harbor guardian",
                DescriptionEn = "An ancient harbor of clockwork machines and armored sea creatures."
            });
            store.TrySaveChapterScenery(run.Id, 2, new(seed, 0, "builtin"));
            run = store.Update(run.Id, value => value.Draft = new()
                { Number = 4, Concept = "Already prepared future concept", NewMechanic = "Timed waves" });
            var before = Combat(run.Levels[3]);
            var progress = LivingArcadeSceneryArtTests.Progress(run);
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            var reopened = store.Load(run.Id)!;
            var result = store.GetBossArt(run.Id, 3);
            await File.WriteAllTextAsync(Path.Combine(output, "boss-native-result.json"), JsonSerializer.Serialize(new
            {
                elapsedSeconds = clock.Elapsed.TotalSeconds, calls, result,
                progressUnchanged = progress == LivingArcadeSceneryArtTests.Progress(reopened),
                combatUnchanged = before == Combat(reopened.Levels[3]),
                weightsDownloaded = false, generatedByActualModel = result?.Outcome == "generated"
            }, LivingArcadeLevelPolicy.JsonOptions), deadline.Token);
            Assert.IsNotNull(result);
            Assert.AreEqual("generated", result.Outcome, "Packaged fallback is not proof of a successful model response.");
            Assert.IsNotNull(result.Artwork);
            Assert.AreEqual("model", result.Artwork.Source);
            Assert.AreEqual(before, Combat(reopened.Levels[3]));
            Assert.AreEqual(progress, LivingArcadeSceneryArtTests.Progress(reopened));
            Assert.IsTrue(calls.Count is >= 1 and <= 2, "Only boss art, with at most one completed-content correction.");
            Assert.IsTrue(calls.All(call => call.ModelId == LivingArcadeModels.MainArtifactId
                && call.Stage == "Arcade.Ai.DrawingBoss"));
            await LivingArcadeBossArtRenderTests.RenderGenuineBossArtAsync(result.Artwork, output);
            var count = calls.Count;
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            Assert.AreEqual(count, calls.Count, "A saved boss appearance must not reroll on repeat preparation.");
        }
        finally
        {
            try { await generation.RetireAsync(); }
            finally { ComponentLicenseGate.ConfirmAsync = previousGate; ApplicationBackgroundOperations.Current = previousBackground; }
        }
    }

    private static string Combat(LivingArcadeLevel level) => JsonSerializer.Serialize(level with
        { BossArt = null }, LivingArcadeLevelPolicy.JsonOptions);
}
