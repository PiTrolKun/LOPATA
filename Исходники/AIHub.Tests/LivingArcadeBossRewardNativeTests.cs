using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeBossRewardNativeTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ActualDesignerCreatesAndCheckpointsABossRewardWithoutRegeneratingPreparedLevels()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_REWARD_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output))
        { Assert.Inconclusive("Explicit reward native directory and genuine application model receipts are required."); return; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"),
            Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
        var ids = new[] { LivingArcadeModels.MainArtifactId, LivingArcadeModels.ArtistArtifactId };
        var receipts = licenses.ReadReceipts();
        var pending = licenses.Entries.Where(entry => ids.Contains(entry.Id)
            && !receipts.Any(receipt => receipt.Id == entry.Id && receipt.Terms == entry.Terms)).Select(entry => entry.Id).ToArray();
        if (pending.Length > 0)
        { Assert.Inconclusive("Current genuine component acknowledgement required: " + string.Join(", ", pending)); return; }
        var previousGate = ComponentLicenseGate.ConfirmAsync;
        var previousBackground = ApplicationBackgroundOperations.Current;
        Assert.IsNull(previousBackground);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var generation = new LivingArcadeGeneration(new(new UserProfileStore(), new IpLocationService()));
        var calls = new List<LivingArcadeInferenceMetric>();
        var index = 0;
        generation.InferenceCompleted += calls.Add;
        generation.InferenceTrace += trace => File.WriteAllText(Path.Combine(output, $"reward-inference-{++index:D2}.json"),
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
            var store = new LivingArcadeRunStore(Path.Combine(output, "isolated-runs"));
            var run = store.Create("Physical boss reward", 734_169);
            run = store.Update(run.Id, value =>
            {
                value.CurrentLevel = 3;
                value.Levels[3] = LivingArcadeLevelPolicy.CreateFirst(value.Seed) with { Number = 3, Authored = false };
                value.Levels[4] = LivingArcadeLevelPolicy.CreateFirst(value.Seed) with { Number = 4, Authored = false };
                value.Draft = new() { Number = 5, Concept = "Already prepared next concept", NewMechanic = "wave" };
            });
            var originalNext = JsonSerializer.Serialize(run.Levels[4], LivingArcadeLevelPolicy.JsonOptions);
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            run = store.Load(run.Id)!;
            Assert.IsTrue(run.BossRewards.TryGetValue(3, out var reward));
            LivingArcadeRewardPolicy.Validate(reward!);
            Assert.AreEqual(originalNext, JsonSerializer.Serialize(run.Levels[4], LivingArcadeLevelPolicy.JsonOptions));
            Assert.IsTrue(calls.Count is >= 1 and <= 3);
            Assert.IsTrue(calls.All(call => call.ModelId == LivingArcadeModels.MainArtifactId && call.Stage == "Arcade.Ai.Reward"));
            var countBefore = calls.Count;
            await generation.PrepareAheadAsync(store, run.Id, ready.Paths!, "ru", deadline.Token);
            Assert.AreEqual(countBefore, calls.Count, "A confirmed reward must not be generated again.");
            await File.WriteAllTextAsync(Path.Combine(output, "accepted-boss-reward.json"),
                JsonSerializer.Serialize(reward, LivingArcadeLevelPolicy.JsonOptions), deadline.Token);
            var second = store.Create("Later boss, different weapon", 284_731);
            second = store.Update(second.Id, value =>
            {
                value.CurrentLevel = 6;
                value.EntryState.WeaponKind = "shotgun";
                value.Levels[6] = LivingArcadeLevelPolicy.CreateFirst(value.Seed) with { Number = 6, Authored = false };
                value.Levels[7] = LivingArcadeLevelPolicy.CreateFirst(value.Seed) with { Number = 7, Authored = false };
                value.Draft = new() { Number = 8, Concept = "Already prepared later concept", NewMechanic = "wave" };
                value.BossRewards[3] = LivingArcadeRewardPolicy.Copy(reward!);
            });
            var secondCallsStart = calls.Count;
            await generation.PrepareAheadAsync(store, second.Id, ready.Paths!, "ru", deadline.Token);
            second = store.Load(second.Id)!;
            Assert.IsTrue(second.BossRewards.TryGetValue(6, out var nextReward));
            LivingArcadeRewardPolicy.Validate(nextReward!);
            Assert.IsTrue(calls.Count - secondCallsStart is >= 1 and <= 3);
            Assert.IsTrue(calls.All(call => call.ModelId == LivingArcadeModels.MainArtifactId && call.Stage == "Arcade.Ai.Reward"));
            Assert.AreNotEqual(Signature(reward!), Signature(nextReward!), "A later boss reward should vary its actual mechanics, not merely its title.");
            var afterSecond = calls.Count;
            await generation.PrepareAheadAsync(store, second.Id, ready.Paths!, "ru", deadline.Token);
            Assert.AreEqual(afterSecond, calls.Count);
            await File.WriteAllTextAsync(Path.Combine(output, "accepted-later-boss-reward.json"),
                JsonSerializer.Serialize(nextReward, LivingArcadeLevelPolicy.JsonOptions), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(output, "reward-native-result.json"), JsonSerializer.Serialize(new
            {
                elapsedSeconds = clock.Elapsed.TotalSeconds, calls, reward, laterReward = nextReward,
                mechanicallyDistinctFromPreviousReward = true, laterWeapon = "shotgun",
                generatedByActualModel = true, reusedOnSecondRun = true, nextLevelWasUnchanged = true,
                modelReceiptsWereNotModified = true, weightsDownloaded = false
            }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
        }
        finally
        {
            try { await generation.RetireAsync(); }
            finally { ComponentLicenseGate.ConfirmAsync = previousGate; ApplicationBackgroundOperations.Current = previousBackground; }
        }
    }

    private static (string Pattern, double Damage, double FireRate, int Pierce, int Stages) Signature(LivingArcadeBossReward reward) =>
        (reward.ShotPattern, reward.DamageMultiplier, reward.FireRateMultiplier, reward.Pierce, reward.DurationLevels);
}
