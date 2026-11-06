using AIHub.Models;
using AIHub.Services.LivingArcade;
using System.Reflection;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePowerupBalanceTests
{
    private static LivingArcadeLevel Quiet(params LivingArcadeMiniEvent[] events) =>
        LivingArcadeLevelPolicy.CreateFirst(89) with
        {
            DurationSeconds = 300,
            Waves = [new() { At = 250, Count = 1, X = .8 }],
            MiniEvents = events.ToList()
        };

    private static LivingArcadeMiniEvent Bonus(string kind, double at = 0) =>
        new() { At = at, X = .5, BonusKind = kind };

    private static void Advance(LivingArcadeSimulation simulation, double seconds,
        LivingArcadeInput input = default)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, input);
    }

    private static void CollectFirst(LivingArcadeSimulation simulation)
    {
        for (var i = 0; i < 900 && simulation.Snapshot.BonusCount == 0; i++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1, simulation.Snapshot.BonusCount, "The pickup must be collected through the real collision path.");
    }

    private static void HoldFireForContactFixture(LivingArcadeSimulation simulation) =>
        // These tests require actual body contact; continuous fire is separately checked to prevent it.
        typeof(LivingArcadeSimulation).GetField("_fireClock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(simulation, 100d);

    [TestMethod]
    [DataRow("power")]
    [DataRow("rapid")]
    [DataRow("spread")]
    [DataRow("shield")]
    public void StrongPowerupsExpireWithinTenActiveRealSecondsEvenDuringSlowWorldTime(string kind)
    {
        var simulation = new LivingArcadeSimulation(Quiet(Bonus(kind)), new());
        CollectFirst(simulation);
        var activated = simulation.Snapshot;
        Assert.AreEqual(kind, activated.PowerupEffect);
        Assert.IsTrue(activated.PowerupSeconds is > 0 and <= 10);
        simulation.SetPaused(true);
        Advance(simulation, 20);
        Assert.AreEqual(activated.PowerupSeconds, simulation.Snapshot.PowerupSeconds);
        simulation.SetPaused(false);
        Advance(simulation, 4, new(SlowDown: true));
        Assert.AreEqual(activated.PowerupSeconds - 4, simulation.Snapshot.PowerupSeconds, .02);
        Advance(simulation, 6.1);
        Assert.AreEqual("", simulation.Snapshot.PowerupEffect);
        Assert.AreEqual(0, simulation.Snapshot.PowerupSeconds);
        Assert.AreEqual(1, simulation.Snapshot.ProjectileDamage, 1e-8);
        Assert.AreEqual(5, simulation.Snapshot.FireRate, 1e-8);
        Assert.AreEqual(1, simulation.Snapshot.SpreadShots);
    }

    [TestMethod]
    public void ReplacingPowerWithRapidFireDoesNotStackMultipliersOrLeakIntoSavedHero()
    {
        var simulation = new LivingArcadeSimulation(Quiet(Bonus("power"), Bonus("rapid", 2)), new());
        CollectFirst(simulation);
        Assert.AreEqual("power", simulation.Snapshot.PowerupEffect);
        Assert.IsTrue(simulation.Snapshot.ProjectileDamage > 1);
        Advance(simulation, 2.1);
        Assert.AreEqual(2, simulation.Snapshot.BonusCount);
        Assert.AreEqual("rapid", simulation.Snapshot.PowerupEffect);
        Assert.AreEqual(1, simulation.Snapshot.ProjectileDamage, 1e-8);
        Assert.IsTrue(simulation.Snapshot.FireRate > 5);
        Assert.AreEqual(1, simulation.ResultHero.WeaponPower);
        Assert.AreEqual(5, simulation.ResultHero.FireRate);
        Assert.AreEqual(1, simulation.ResultHero.SpreadShots);
        var next = new LivingArcadeSimulation(Quiet(), simulation.ResultHero);
        Assert.AreEqual("", next.Snapshot.PowerupEffect);
        Assert.AreEqual(1, next.Snapshot.ProjectileDamage, 1e-8);
        Assert.AreEqual(5, next.Snapshot.FireRate);
    }

    [TestMethod]
    public void RepeatedStrongPickupsDoNotRefreshTheOriginalEffectWindow()
    {
        var simulation = new LivingArcadeSimulation(Quiet(Bonus("power"), Bonus("power", 2),
            Bonus("power", 4)), new());
        CollectFirst(simulation);
        var originalDuration = simulation.Snapshot.PowerupSeconds;
        Advance(simulation, 4.1);
        Assert.AreEqual(3, simulation.Snapshot.BonusCount);
        Assert.AreEqual(originalDuration - 4.1, simulation.Snapshot.PowerupSeconds, .02);
        Advance(simulation, originalDuration - 4.1 + .1);
        Assert.AreEqual("", simulation.Snapshot.PowerupEffect);
        Assert.AreEqual(1, simulation.Snapshot.ProjectileDamage, 1e-8);
    }

    [TestMethod]
    public void ProtectionPreventsRealContactDamageOnlyWhileItsTimerIsActive()
    {
        var level = Quiet(Bonus("shield")) with
        {
            EnemyTypes = [new() { Id = "contact", Health = 200, Speed = .07 }],
            Waves = [new() { At = 0, EnemyId = "contact", Count = 1, X = .5 },
                new() { At = 2, EnemyId = "contact", Count = 1, X = .5 }]
        };
        var protectedHero = new LivingArcadeSimulation(level, new());
        var control = new LivingArcadeSimulation(level with { MiniEvents = [] }, new());
        HoldFireForContactFixture(protectedHero); HoldFireForContactFixture(control);
        Advance(protectedHero, 13); Advance(control, 13);
        Assert.AreEqual(0, protectedHero.Snapshot.DamageTaken);
        Assert.IsTrue(control.Snapshot.DamageTaken > 0);
        Advance(protectedHero, 2);
        Assert.AreEqual("", protectedHero.Snapshot.PowerupEffect);
        Assert.IsTrue(protectedHero.Snapshot.DamageTaken > 0,
            "Protection must not silently remain after its visible timer ends.");
    }

    [TestMethod]
    public void PermanentWeaponPickupAddsOnlyASmallAmountAndLeavesOtherStatsUntouched()
    {
        var simulation = new LivingArcadeSimulation(Quiet(Bonus("weapon")), new());
        CollectFirst(simulation);
        Assert.IsTrue(simulation.Snapshot.ProjectileDamage is > 1 and <= 1.05);
        Assert.AreEqual(5, simulation.Snapshot.FireRate);
        Assert.AreEqual(1, simulation.Snapshot.SpreadShots);
        Assert.AreEqual(100, simulation.Snapshot.MaxHealth);
        Assert.AreEqual("", simulation.Snapshot.PowerupEffect);
        Advance(simulation, 11);
        Assert.IsTrue(simulation.Snapshot.ProjectileDamage is > 1 and <= 1.05);
        Assert.AreEqual(simulation.Snapshot.ProjectileDamage, simulation.ResultHero.WeaponPower, 1e-8);
    }

    [TestMethod]
    public void AutomaticPickupSequenceIsVariedVisibleAndRepeatableForTheSameSavedLevel()
    {
        var level = Quiet(Enumerable.Range(0, 18).Select(i =>
            new LivingArcadeMiniEvent { At = 0, X = .1 + (i % 9) * .1 }).ToArray());
        var first = new LivingArcadeSimulation(level, new());
        var second = new LivingArcadeSimulation(level, new());
        first.Advance(1d / 60, default);
        second.Advance(1d / 60, default);
        CollectionAssert.AreEqual(first.Snapshot.Pickups.Select(p => p.Id).ToArray(),
            second.Snapshot.Pickups.Select(p => p.Id).ToArray());
        Assert.IsTrue(first.Snapshot.Pickups.Select(p => p.Id).Distinct().Count() >= 6,
            "The automatic pool should offer more than health, a fan and protection.");
    }

    [TestMethod]
    public void LevelTempoGrowsGraduallyAndAdvancesTheWorldWithoutChangingRealCooldowns()
    {
        var first = new LivingArcadeSimulation(Quiet(), new());
        var second = new LivingArcadeSimulation(Quiet() with { Number = 2 }, new());
        var tenth = new LivingArcadeSimulation(Quiet() with { Number = 10 }, new());
        Assert.AreEqual(1, first.Snapshot.TempoMultiplier, 1e-8);
        Assert.IsTrue(second.Snapshot.TempoMultiplier is > 1 and <= 1.06);
        Assert.IsTrue(tenth.Snapshot.TempoMultiplier > second.Snapshot.TempoMultiplier);
        Advance(first, 1, new(SpeedUp: true));
        Advance(second, 1, new(SpeedUp: true));
        Assert.IsTrue(second.Snapshot.Progress > first.Snapshot.Progress);
        Assert.AreEqual(first.Snapshot.SpeedUpCooldown, second.Snapshot.SpeedUpCooldown, 1e-8);
        Assert.AreEqual(first.Snapshot.EffectSeconds, second.Snapshot.EffectSeconds, 1e-8);
    }

    [TestMethod]
    public void NegativePickupPursuesLaterallyButCanBeDodgedAndCannotChaseForever()
    {
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent()
            { At = 0, Kind = "negative", BonusKind = "jam", X = .1 }), new());
        Advance(simulation, 2);
        var pursuing = simulation.Snapshot.Pickups.Single();
        Assert.AreEqual("negative_jam", pursuing.Id);
        Assert.IsTrue(pursuing.X > .2 && pursuing.X < .5);
        Assert.IsTrue(pursuing.Hostile);
        Advance(simulation, 8, new(Right: true));
        Assert.AreEqual("", simulation.Snapshot.NegativeEffect);
        Assert.AreEqual(5, simulation.Snapshot.FireRate);
        Assert.AreEqual(0, simulation.Snapshot.Pickups.Count,
            "A missed negative pickup must fly past instead of following indefinitely.");
    }

    [TestMethod]
    public void NegativePickupEffectIsTemporaryPauseSafeAndNeverChangesSavedStats()
    {
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent()
            { At = 0, Kind = "negative", BonusKind = "jam", X = .5 }), new());
        for (var i = 0; i < 600 && simulation.Snapshot.NegativeEffect.Length == 0; i++)
            simulation.Advance(1d / 60, default);
        var jammed = simulation.Snapshot;
        Assert.AreEqual("jam", jammed.NegativeEffect);
        Assert.IsTrue(jammed.NegativeSeconds is > 0 and <= 6);
        Assert.IsTrue(jammed.FireRate > 0 && jammed.FireRate < 5);
        Assert.IsTrue(jammed.RecentBonus!.IsNegative);
        Assert.AreEqual(5, simulation.ResultHero.FireRate);
        simulation.SetPaused(true); Advance(simulation, 10);
        Assert.AreEqual(jammed.NegativeSeconds, simulation.Snapshot.NegativeSeconds);
        simulation.SetPaused(false); Advance(simulation, 6.1, new(SlowDown: true));
        Assert.AreEqual("", simulation.Snapshot.NegativeEffect);
        Assert.AreEqual(0, simulation.Snapshot.NegativeSeconds);
        Assert.AreEqual(5, simulation.Snapshot.FireRate);
    }

    [TestMethod]
    public void NegativePickupBurstDoesNotCreateAnUnavoidableSwarm()
    {
        var simulation = new LivingArcadeSimulation(Quiet(Enumerable.Range(0, 20)
            .Select(_ => new LivingArcadeMiniEvent { At = 0, Kind = "negative", X = .5 }).ToArray()), new());
        simulation.Advance(1d / 60, default);
        Assert.AreEqual(2, simulation.Snapshot.Pickups.Count);
        Assert.IsTrue(simulation.Snapshot.Pickups.All(p => p.Id.StartsWith("negative_", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DestroyedEnemiesGrantExperienceAndResultHeroCarriesItToTheNextSession()
    {
        var level = Quiet() with
        {
            EnemyTypes = [new() { Id = "target", Health = .25, Speed = .2 }],
            Waves = Enumerable.Range(0, 10).Select(i => new LivingArcadeWave
                { At = 1 + i * 1.5, EnemyId = "target", Count = 1, X = .5 }).ToList()
        };
        var simulation = new LivingArcadeSimulation(level, new());
        for (var i = 0; i < 1800 && simulation.ResultHero.TotalKills < 10; i++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(10L, simulation.ResultHero.TotalKills);
        Assert.AreEqual(2, simulation.Snapshot.CharacterLevel);
        Assert.AreEqual(0L, simulation.Snapshot.KillsTowardNextLevel);
        Assert.AreEqual(20L, simulation.Snapshot.KillsRequiredForNextLevel);
        Assert.AreEqual(1.025, simulation.Snapshot.ProjectileDamage, 1e-8);
        var resumed = new LivingArcadeSimulation(Quiet(), simulation.ResultHero);
        Assert.AreEqual(2, resumed.Snapshot.CharacterLevel);
        Assert.AreEqual(10L, resumed.ResultHero.TotalKills);
        Assert.AreEqual(simulation.Snapshot.ProjectileDamage, resumed.Snapshot.ProjectileDamage, 1e-8);
    }

    [TestMethod]
    public void DespawningAndContactingEnemiesDoNotGrantExperience()
    {
        var level = Quiet() with
        {
            EnemyTypes = [new() { Id = "survivor", Health = 200, Speed = .2 }],
            Waves = [new() { At = 0, EnemyId = "survivor", Count = 1, X = .1 },
                new() { At = 0, EnemyId = "survivor", Count = 1, X = .5 }]
        };
        var simulation = new LivingArcadeSimulation(level, new());
        HoldFireForContactFixture(simulation);
        Advance(simulation, 10);
        Assert.AreEqual(0, simulation.Snapshot.Enemies.Count);
        Assert.AreEqual(14, simulation.Snapshot.DamageTaken, 1e-8);
        Assert.AreEqual(0L, simulation.ResultHero.TotalKills);
        Assert.AreEqual(1, simulation.Snapshot.CharacterLevel);
    }

    [TestMethod]
    public void ExperienceCheckpointSurvivesRestartAndDuplicateOrOlderCheckpointCannotRollItBack()
    {
        WithStore(directory =>
        {
            var store = new LivingArcadeRunStore(directory);
            var run = store.Create("Experience", 31);
            store.RecordExperience(run.Id, 12);
            var reopened = new LivingArcadeRunStore(directory);
            var checkpoint = reopened.Load(run.Id)!;
            Assert.AreEqual(12L, checkpoint.EntryState.TotalKills);
            reopened.RecordExperience(run.Id, 12);
            reopened.RecordExperience(run.Id, 4);
            var unchanged = reopened.Load(run.Id)!;
            Assert.AreEqual(12L, unchanged.EntryState.TotalKills);
            Assert.AreEqual(checkpoint.Revision, unchanged.Revision,
                "Duplicate and stale checkpoints should not rewrite a saved run.");
            reopened.RecordExperience(run.Id, 30);
            var latest = new LivingArcadeRunStore(directory).Load(run.Id)!;
            Assert.AreEqual(30L, latest.EntryState.TotalKills);
            Assert.AreEqual(3, new LivingArcadeSimulation(Quiet(), latest.EntryState).Snapshot.CharacterLevel);
        });
    }

    [TestMethod]
    public void DefeatKeepsEarnedExperienceWithoutCommittingUnfinishedPickupUpgrades()
    {
        WithStore(directory =>
        {
            var store = new LivingArcadeRunStore(directory);
            var run = store.Create("Defeat", 32);
            store.Update(run.Id, current => current.EntryState = new()
                { PowerupBalanceVersion = 1, WeaponPower = 1.1, TotalKills = 7 });
            store.RecordDefeat(run.Id, 40, new()
            {
                PowerupBalanceVersion = 1, TotalKills = 17, WeaponPower = 1.3,
                FireRate = 5.8, MaxHealth = 118, SpreadShots = 3
            });
            var reopened = new LivingArcadeRunStore(directory);
            var defeated = reopened.Load(run.Id)!;
            Assert.AreEqual(17L, defeated.EntryState.TotalKills);
            Assert.AreEqual(1.1, defeated.EntryState.WeaponPower, 1e-8);
            Assert.AreEqual(5, defeated.EntryState.FireRate);
            Assert.AreEqual(100, defeated.EntryState.MaxHealth);
            Assert.AreEqual(1, defeated.EntryState.SpreadShots);
            Assert.AreEqual(1, defeated.Attempts);
            reopened.RecordDefeat(run.Id, 20, new() { TotalKills = 4 });
            Assert.AreEqual(17L, new LivingArcadeRunStore(directory).Load(run.Id)!.EntryState.TotalKills);
        });
    }

    private static void WithStore(Action<string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-powerup-save-" + Guid.NewGuid().ToString("N"));
        try { test(directory); }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("arcade-powerup-save-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected temporary test path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
