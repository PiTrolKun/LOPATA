using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBossRewardTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-boss-reward-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-boss-reward-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    internal static LivingArcadeBossReward Reward(int duration = 2) => LivingArcadeGeneration.ParseBossReward(JsonSerializer.Serialize(new
    {
        titleRu = "Солнечная волна", titleEn = "Solar wave",
        descriptionRu = "Волнистые снаряды наносят на 40% больше урона.",
        descriptionEn = "Waving projectiles deal 40% more damage.", durationLevels = duration,
        damageMultiplier = 1.4, fireRateMultiplier = 1d, shotPattern = "wave", pierce = 0, colorHex = "#F8D16D"
    }));

    private LivingArcadeRun PrepareBoss(LivingArcadeRunStore store)
    {
        var run = store.Create("Reward test", 91);
        return store.Update(run.Id, value =>
        {
            value.CurrentLevel = 3;
            value.Levels[3] = LivingArcadeLevelPolicy.CreateFirst(91) with { Number = 3, Authored = false };
            value.Levels[4] = LivingArcadeLevelPolicy.CreateFirst(91) with { Number = 4, Authored = false };
        });
    }

    [TestMethod]
    public void ModelReceiptIsAttachedAndUnknownCodeOrUnboundedEffectsAreRejected()
    {
        var reward = Reward();
        Assert.AreEqual("boss_reward", reward.Credits.Single().Role);
        Assert.AreEqual(LivingArcadeModels.MainRevision, reward.Credits.Single().Revision);
        Assert.IsTrue(reward.GeneratedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        foreach (var invalid in new[] { reward with { DurationLevels = 3 }, reward with { DamageMultiplier = double.NaN },
            reward with { ShotPattern = "execute_csharp" }, reward with { DamageMultiplier = 2, FireRateMultiplier = 1.5, ShotPattern = "fan", Pierce = 2 },
            reward with { DamageMultiplier = 1, FireRateMultiplier = 1, ShotPattern = "straight" },
            reward with { DamageMultiplier = 1, FireRateMultiplier = 1, ShotPattern = "wave" },
            reward with { DamageMultiplier = 1, FireRateMultiplier = 1, ShotPattern = "returning" }, reward with { Credits = [] } })
            Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeRewardPolicy.Validate(invalid));
        LivingArcadeRewardPolicy.Validate(reward with { DamageMultiplier = 1, FireRateMultiplier = 1, ShotPattern = "fan" });
        var json = JsonSerializer.Serialize(reward, LivingArcadeLevelPolicy.JsonOptions);
        Assert.ThrowsExactly<JsonException>(() => LivingArcadeGeneration.ParseBossReward(json[..^1] + ",\"code\":\"danger()\"}"));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeGeneration.ParseBossReward(json[..^1] + ",\"durationLevels\":1}"));
    }

    [TestMethod]
    public void CompletedBossWaitsForRealRewardThenClaimsItExactlyOnce()
    {
        var store = new LivingArcadeRunStore(_root); var run = PrepareBoss(store);
        store.CompleteLevel(run.Id, new(), new() { IsComplete = true, BossDefeated = true });
        Assert.IsFalse(store.TryAdvanceToReadyLevel(run.Id, out var waiting));
        Assert.AreEqual(3, waiting!.CurrentLevel);
        Assert.AreEqual(3, waiting.PendingBossRewardLevel);
        Assert.IsNotNull(waiting.PendingHero);
        var reward = Reward();
        Assert.IsTrue(store.TrySaveBossReward(run.Id, 3, reward));
        Assert.IsFalse(store.TrySaveBossReward(run.Id, 3, Reward(1)));
        Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var advanced));
        Assert.AreEqual(4, advanced!.CurrentLevel);
        Assert.IsNull(advanced.PendingBossRewardLevel);
        Assert.AreEqual(2, advanced.EntryState.ActiveBossReward!.RemainingLevels);
        Assert.AreEqual(4, advanced.EntryState.ActiveBossReward.StartLevel);
        Assert.AreEqual(reward.TitleRu, advanced.EntryState.ActiveBossReward.Reward.TitleRu);
        Assert.IsFalse(store.TryAdvanceToReadyLevel(run.Id, out _));
        reward.Credits.Clear();
        Assert.AreEqual(1, store.Load(run.Id)!.BossRewards[3].Credits.Count);
    }

    [TestMethod]
    public void RewardSurvivesDefeatAndRestartButExpiresAfterItsFollowingCompletedStages()
    {
        var store = new LivingArcadeRunStore(_root); var run = PrepareBoss(store);
        store.TrySaveBossReward(run.Id, 3, Reward());
        store.CompleteLevel(run.Id, new(), new() { IsComplete = true, BossDefeated = true });
        Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var stage4));
        run = stage4!;
        var active = run.EntryState.ActiveBossReward!;
        store.RecordDefeat(run.Id, 20, run.EntryState);
        run = new LivingArcadeRunStore(_root).Load(run.Id)!;
        Assert.AreEqual(2, run.EntryState.ActiveBossReward!.RemainingLevels);
        Assert.AreEqual(active.Reward.TitleRu, run.EntryState.ActiveBossReward.Reward.TitleRu);
        store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(91) with { Number = 5, Authored = false });
        store.CompleteLevel(run.Id, run.EntryState, new() { IsComplete = true });
        Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var stage5));
        run = stage5!;
        Assert.AreEqual(1, run.EntryState.ActiveBossReward!.RemainingLevels);
        store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(91) with { Number = 6, Authored = false });
        store.CompleteLevel(run.Id, run.EntryState, new() { IsComplete = true });
        Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var stage6));
        run = stage6!;
        Assert.IsNull(run.EntryState.ActiveBossReward);
        Assert.AreEqual(6, run.CurrentLevel);
    }

    [TestMethod]
    public void RewardPublicationRejectsDeletedOrAdvancedRunsAndInvalidReceipts()
    {
        var store = new LivingArcadeRunStore(_root); var run = PrepareBoss(store);
        Assert.IsFalse(store.TrySaveBossReward(run.Id, 6, Reward()));
        Assert.ThrowsExactly<InvalidDataException>(() => store.TrySaveBossReward(run.Id, 3, Reward() with { Credits = [] }));
        store.Delete(run.Id);
        Assert.IsFalse(store.TrySaveBossReward(run.Id, 3, Reward()));
        Assert.IsNull(store.Load(run.Id));
    }
}
