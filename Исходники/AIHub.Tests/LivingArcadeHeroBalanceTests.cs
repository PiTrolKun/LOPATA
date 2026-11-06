using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeHeroBalanceTests
{
    [TestMethod]
    public void OldSaveKeepsASmallShareOfProgressWithoutMutatingItsSource()
    {
        var saved = JsonSerializer.Deserialize<LivingArcadeHeroState>(
            """{"permanentPower":1.3,"weaponPower":3.98,"maxHealth":140,"fireRate":8,"spreadShots":5}""",
            LivingArcadeLevelPolicy.JsonOptions)!;

        var balanced = LivingArcadeHeroBalance.Normalize(saved);

        Assert.AreEqual(1.03, balanced.PermanentPower, 1e-10);
        Assert.AreEqual(1.1192, balanced.WeaponPower, 1e-10);
        Assert.AreEqual(5.48, balanced.FireRate, 1e-10);
        Assert.AreEqual(104, balanced.MaxHealth);
        Assert.AreEqual(1, balanced.SpreadShots);
        Assert.AreEqual(1, balanced.PowerupBalanceVersion);
        Assert.AreEqual(3.98, saved.WeaponPower);
        Assert.AreEqual(5, saved.SpreadShots);
        Assert.AreEqual(0, saved.PowerupBalanceVersion);
    }

    [TestMethod]
    public void ReopeningANewBalanceSaveDoesNotCompressItAgain()
    {
        var first = LivingArcadeHeroBalance.Normalize(new()
        { WeaponPower = 4, PermanentPower = 2, FireRate = 9, MaxHealth = 180, SpreadShots = 5,
            WeaponKind = "shotgun", Companions = [new() { Id = "survivor", Health = 18, MaxHealth = 24 }] });
        var reopened = JsonSerializer.Deserialize<LivingArcadeHeroState>(
            JsonSerializer.Serialize(first, LivingArcadeLevelPolicy.JsonOptions),
            LivingArcadeLevelPolicy.JsonOptions)!;

        var normalized = LivingArcadeHeroBalance.Normalize(reopened);
        Assert.AreEqual(JsonSerializer.Serialize(first, LivingArcadeLevelPolicy.JsonOptions),
            JsonSerializer.Serialize(normalized, LivingArcadeLevelPolicy.JsonOptions));
        Assert.AreNotSame(reopened.Companions, normalized.Companions);
        Assert.AreNotSame(reopened.Companions.Single(), normalized.Companions.Single());
        normalized.Companions.Single().Health = 1;
        normalized.Companions.Clear();
        Assert.AreEqual(18d, reopened.Companions.Single().Health);
        Assert.AreEqual(18d, first.Companions.Single().Health);
    }

    [TestMethod]
    public void BothOldAndNewBalanceStatsRemainWithinSmallPermanentCeilings()
    {
        foreach (var version in new[] { 0, 1 })
        {
            var balanced = LivingArcadeHeroBalance.Normalize(new()
            { PowerupBalanceVersion = version, WeaponPower = 100, PermanentPower = 100,
                FireRate = 20, MaxHealth = 1000, SpreadShots = 5 });
            Assert.AreEqual(1.35, balanced.WeaponPower, 1e-10);
            Assert.AreEqual(1.25, balanced.PermanentPower, 1e-10);
            Assert.AreEqual(6, balanced.FireRate);
            Assert.AreEqual(120, balanced.MaxHealth);
            Assert.AreEqual(1, balanced.SpreadShots);
        }
    }

    [TestMethod]
    public void NewRunHasNoUnintendedStartingUpgrade()
    {
        var balanced = LivingArcadeHeroBalance.Normalize(new());
        Assert.AreEqual(1, balanced.WeaponPower);
        Assert.AreEqual(1, balanced.PermanentPower);
        Assert.AreEqual(5, balanced.FireRate);
        Assert.AreEqual(100, balanced.MaxHealth);
        Assert.AreEqual(1, balanced.SpreadShots);
    }

    [TestMethod]
    public void LevelTransferNormalizesLegacyProgressOnceAndNeverCarriesTheFan()
    {
        var old = new LivingArcadeHeroState
        { WeaponPower = 6, PermanentPower = 2, FireRate = 10, MaxHealth = 200, SpreadShots = 5 };
        var destination = LivingArcadeLevelPolicy.CreateFirst(7) with
        { ResetDifficulty = false, CarryFraction = .5 };

        var result = LivingArcadeLevelPolicy.Transfer(old, destination);

        Assert.AreEqual(1.1, result.WeaponPower, 1e-10);
        Assert.AreEqual(1.1, result.PermanentPower, 1e-10);
        Assert.AreEqual(5.4, result.FireRate, 1e-10);
        Assert.AreEqual(105, result.MaxHealth);
        Assert.AreEqual(1, result.SpreadShots);
        Assert.AreEqual(1, result.PowerupBalanceVersion);
        Assert.AreEqual(0, old.PowerupBalanceVersion);
    }

    [TestMethod]
    public void KillExperienceDoublesTheRequirementAfterEachHeroLevel()
    {
        Assert.AreEqual((1, 0L, 10L), LivingArcadeHeroBalance.Progress(0));
        Assert.AreEqual((1, 9L, 10L), LivingArcadeHeroBalance.Progress(9));
        Assert.AreEqual((2, 0L, 20L), LivingArcadeHeroBalance.Progress(10));
        Assert.AreEqual((2, 19L, 20L), LivingArcadeHeroBalance.Progress(29));
        Assert.AreEqual((3, 0L, 40L), LivingArcadeHeroBalance.Progress(30));
        Assert.AreEqual((4, 0L, 80L), LivingArcadeHeroBalance.Progress(70));
        var maximum = LivingArcadeHeroBalance.Progress(long.MaxValue);
        Assert.AreEqual(60, maximum.Level);
        Assert.IsTrue(maximum.KillsTowardNextLevel >= 0);
        Assert.IsTrue(maximum.KillsTowardNextLevel < maximum.KillsRequiredForNextLevel);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LivingArcadeHeroBalance.Progress(-1));
    }

    [TestMethod]
    public void NormalizationAndLevelResetKeepEarnedExperienceAndAllowFragileTestHeroes()
    {
        var hero = new LivingArcadeHeroState { TotalKills = 70, MaxHealth = 1 };
        var normalized = LivingArcadeHeroBalance.Normalize(hero);
        var transferred = LivingArcadeLevelPolicy.Transfer(hero,
            LivingArcadeLevelPolicy.CreateFirst(9) with { ResetDifficulty = true });

        Assert.AreEqual(70L, normalized.TotalKills);
        Assert.AreEqual(1, normalized.MaxHealth);
        Assert.AreEqual(70L, transferred.TotalKills);
        Assert.AreEqual(100, transferred.MaxHealth);
    }

    [TestMethod]
    public void CompletingWithAnOlderHeroCannotRollBackCheckpointedExperience()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "arcade-balance-checkpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory);
            var run = store.Create("Checkpoint", 71);
            store.RecordExperience(run.Id, 30);
            store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(72) with
                { Number = 2, ResetDifficulty = true });
            var completed = new LivingArcadeSnapshot { IsComplete = true, Progress = 120 };
            var stale = new LivingArcadeHeroState { TotalKills = 20, WeaponPower = 3 };
            store.CompleteLevel(run.Id, stale, completed);
            Assert.AreEqual(30L, store.Load(run.Id)!.PendingHero!.TotalKills);
            Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var next));
            Assert.AreEqual(30L, next!.EntryState.TotalKills);
            Assert.AreEqual(LivingArcadeHeroBalance.Version, next.EntryState.PowerupBalanceVersion);
            Assert.AreEqual(20L, stale.TotalKills, "Committing a level must not mutate the caller's hero.");
        }
        finally
        {
            var full = System.IO.Path.GetFullPath(directory);
            if (System.IO.Directory.Exists(full)
                && full.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(full).StartsWith("arcade-balance-checkpoint-", StringComparison.Ordinal))
                System.IO.Directory.Delete(full, true);
        }
    }
}
