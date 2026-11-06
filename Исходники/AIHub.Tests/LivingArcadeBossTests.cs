using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBossTests
{
    private static LivingArcadeLevel QuietLevel(int number, bool lingeringEnemy = false) => new()
    {
        Number = number, Authored = true, DurationSeconds = 30, MiniEvents = [],
        EnemyTypes = [new() { Health = .25, Speed = .2,
            States = [new() { Motion = lingeringEnemy ? "hold" : "line" }] }],
        Waves = [new() { At = 0, Count = 1, X = .9, SpawnInterval = 0 }]
    };

    private static LivingArcadeHeroState StrongEarnedHero() => new()
    {
        PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
        WeaponPower = 1.35, PermanentPower = 1.25, FireRate = 6, MaxHealth = 120, TotalKills = 70
    };

    private static void Advance(LivingArcadeSimulation simulation, double seconds, LivingArcadeInput input = default)
    {
        for (var tick = 0; tick < (int)Math.Round(seconds * 60); tick++) simulation.Advance(1d / 60, input);
    }

    private static void AwaitBoss(LivingArcadeSimulation simulation)
    {
        for (var tick = 0; tick < 60 * 60 && simulation.Snapshot.BossPhase == 0
            && !simulation.Snapshot.IsComplete && !simulation.Snapshot.IsDefeated; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1, simulation.Snapshot.BossPhase);
        Assert.IsFalse(simulation.Snapshot.IsComplete);
    }

    [TestMethod]
    public void OnlyEveryThirdGameLevelEndsWithAnActualBoss()
    {
        foreach (var number in new[] { 1, 2, 3, 4, 6 })
        {
            var simulation = new LivingArcadeSimulation(QuietLevel(number), new());
            for (var tick = 0; tick < 40 * 60 && !simulation.Snapshot.IsComplete
                && simulation.Snapshot.BossPhase == 0; tick++) simulation.Advance(1d / 60, default);
            var state = simulation.Snapshot;
            if (number % 3 == 0)
            {
                Assert.AreEqual(1, state.BossPhase);
                Assert.IsFalse(state.IsComplete);
                var boss = state.Enemies.Single(e => e.IsBoss);
                Assert.IsTrue(boss.Radius >= .11);
                Assert.IsTrue(state.BossMaxHealth >= 70);
                Assert.IsTrue(boss.Telegraph);
            }
            else
            {
                Assert.AreEqual(0, state.BossPhase);
                Assert.IsTrue(state.IsComplete);
                Assert.IsFalse(state.Enemies.Any(e => e.IsBoss));
            }
        }
    }

    [TestMethod]
    public void BossWaitsForRegularEnemiesAndPreventsPrematureLevelCompletion()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(3, lingeringEnemy: true), new());
        while (simulation.Snapshot.Progress < 31) simulation.Advance(1d / 60, default);
        Assert.AreEqual(0, simulation.Snapshot.BossPhase);
        Assert.AreEqual(1, simulation.Snapshot.Enemies.Count);
        Assert.IsFalse(simulation.Snapshot.IsComplete);
        AwaitBoss(simulation);
        Assert.AreEqual(1, simulation.Snapshot.Enemies.Count);
        Assert.IsTrue(simulation.Snapshot.Enemies.Single().IsBoss);
    }

    [TestMethod]
    public void ConservativeCostIncludesTheSeparateBossEncounterAndItsLargestVolley()
    {
        var normal = LivingArcadeLevelPolicy.EstimateCost(QuietLevel(2));
        var boss = LivingArcadeLevelPolicy.EstimateCost(QuietLevel(3));
        Assert.AreEqual(normal.ExpandedSpawnCount + 1, boss.ExpandedSpawnCount);
        Assert.AreEqual(normal.PeakScheduledEnemies, boss.PeakScheduledEnemies,
            "The boss does not overlap the regular enemy population.");
        Assert.IsTrue(boss.EstimatedPeakHostileShots >= 89);
        Assert.IsTrue(boss.EstimatedPeakHostileShots <= 480);
    }

    [TestMethod]
    public void RealProjectileHitsReachAllThreePhasesAndAwardThirtyExperienceOnlyAfterDefeat()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(3), StrongEarnedHero());
        AwaitBoss(simulation);
        var phases = new HashSet<int>();
        var previousHealth = simulation.Snapshot.BossHealth;
        for (var tick = 0; tick < 90 * 60 && !simulation.Snapshot.IsComplete
            && !simulation.Snapshot.IsDefeated; tick++)
        {
            var state = simulation.Snapshot;
            if (state.BossPhase > 0)
            {
                phases.Add(state.BossPhase);
                Assert.IsTrue(state.BossHealth <= previousHealth);
                previousHealth = state.BossHealth;
                Assert.AreEqual(70L, simulation.ResultHero.TotalKills,
                    "Changing the phase is not another enemy kill.");
            }
            var boss = state.Enemies.SingleOrDefault(e => e.IsBoss);
            var offset = (boss?.X ?? state.PlayerX) - state.PlayerX;
            simulation.Advance(1d / 60, new(Left: offset < -.015, Right: offset > .015));
        }
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, phases.ToArray());
        Assert.IsTrue(simulation.Snapshot.IsComplete, "A legitimately upgraded hero must be able to finish the boss.");
        Assert.IsFalse(simulation.Snapshot.IsDefeated);
        Assert.AreEqual(100L, simulation.ResultHero.TotalKills);
        Assert.IsTrue(simulation.Snapshot.BossDefeated);
        Advance(simulation, 5);
        Assert.AreEqual(100L, simulation.ResultHero.TotalKills);
        Assert.AreEqual(0, simulation.Snapshot.BossPhase);
        Assert.AreEqual(0, simulation.Snapshot.BossHealth);
    }

    [TestMethod]
    public void BossPausesWithTheGameAndSuperAttackCannotSkipTheWholeFight()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(3), StrongEarnedHero());
        AwaitBoss(simulation);
        var health = simulation.Snapshot.BossHealth;
        simulation.SetPaused(true);
        Advance(simulation, 45, new(Super: true));
        Assert.AreEqual(health, simulation.Snapshot.BossHealth);
        Assert.IsFalse(simulation.Snapshot.IsComplete);
        simulation.SetPaused(false);
        simulation.Advance(1d / 60, new(Super: true));
        Assert.IsTrue(simulation.Snapshot.BossHealth > 0);
        Assert.IsFalse(simulation.Snapshot.IsComplete);
        Assert.AreEqual(70L, simulation.ResultHero.TotalKills);
    }
}
