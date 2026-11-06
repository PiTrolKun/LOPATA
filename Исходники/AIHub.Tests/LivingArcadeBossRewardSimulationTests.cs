using System.IO;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBossRewardSimulationTests
{
    private static LivingArcadeLevel Quiet(int number = 4) => LivingArcadeLevelPolicy.CreateFirst(112) with
    {
        Number = number, Authored = false, DurationSeconds = 300,
        Waves = [new() { At = 250, Count = 1, X = .9 }], MiniEvents = []
    };

    private static LivingArcadeHeroState Hero(string pattern = "straight", double damage = 1.4,
        double rate = 1, int pierce = 0, int duration = 2) => new()
    {
        PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
        ActiveBossReward = new()
        {
            StartLevel = 4, RemainingLevels = duration,
            Reward = LivingArcadeBossRewardTests.Reward(duration) with
            { ShotPattern = pattern, DamageMultiplier = damage, FireRateMultiplier = rate, Pierce = pierce }
        }
    };

    private static void Advance(LivingArcadeSimulation simulation, double seconds)
    { for (var i = 0; i < Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, default); }

    private static LivingArcadeLevel Targets(int count, double health) => Quiet() with
    {
        EnemyTypes = [new() { Health = health, Radius = .03, Speed = .2,
            States = [new() { ForwardSpeed = 3, Duration = 10 }] }],
        Waves = Enumerable.Range(0, count).Select(_ => new LivingArcadeWave
            { At = 0, Count = 1, SpawnInterval = 0, X = .5 }).ToList()
    };

    [TestMethod]
    public void DamageAndFireRateMultipliersChangeActualCombatAndVolleyFrequency()
    {
        // Leave enough approach time that both cases retain the requested 1.5 HP.
        // Otherwise the kill-window guard intentionally makes a very fast target a one-hit kill.
        var target = Targets(1, 1.5);
        target.EnemyTypes[0].Speed = .03;
        var ordinary = new LivingArcadeSimulation(target, new());
        var stronger = new LivingArcadeSimulation(target, Hero(damage: 1.8));
        ordinary.Advance(1d / 60, default); stronger.Advance(1d / 60, default);
        Assert.AreEqual(1.5, ordinary.Snapshot.Enemies.Single().MaxHealth, 1e-10);
        Assert.AreEqual(1.5, stronger.Snapshot.Enemies.Single().MaxHealth, 1e-10);
        for (var i = 0; i < 120 && stronger.ResultHero.TotalKills == 0; i++)
        { ordinary.Advance(1d / 60, default); stronger.Advance(1d / 60, default); }
        Assert.AreEqual(1L, stronger.ResultHero.TotalKills);
        Assert.AreEqual(0L, ordinary.ResultHero.TotalKills);
        Assert.IsTrue(ordinary.Snapshot.Enemies.Single().HealthFraction is > 0 and < 1);

        ordinary = new(Quiet(), new());
        var faster = new LivingArcadeSimulation(Quiet(), Hero(rate: 1.5));
        Advance(ordinary, .5); Advance(faster, .5);
        Assert.AreEqual(ordinary.Snapshot.FireRate * 1.5, faster.Snapshot.FireRate, 1e-10);
        Assert.IsTrue(faster.Snapshot.Shots.Count > ordinary.Snapshot.Shots.Count);
        Assert.IsTrue(faster.Snapshot.Shots.All(s => s.Color == "#F8D16D"));
    }

    [TestMethod]
    public void ModelPatternsCreateARealFanWaveAndReturningProjectiles()
    {
        var fan = new LivingArcadeSimulation(Quiet(), Hero("fan"));
        Advance(fan, 1d / 60);
        Assert.AreEqual(3, fan.Snapshot.Shots.Count);
        Assert.IsTrue(fan.Snapshot.Shots.Min(s => s.X) < .5);
        Assert.IsTrue(fan.Snapshot.Shots.Max(s => s.X) > .5);
        var shotgun = new LivingArcadeSimulation(Quiet(), Hero("fan") with { WeaponKind = "shotgun" });
        Advance(shotgun, 1d / 60);
        Assert.AreEqual(5, shotgun.Snapshot.Shots.Count, "The fan adds power even to a weapon that already shoots three projectiles.");

        var wave = new LivingArcadeSimulation(Quiet(), Hero("wave"));
        Advance(wave, .3);
        Assert.IsTrue(Math.Abs(wave.Snapshot.Shots.MaxBy(s => s.Age)!.X - .5) > .005);

        var returning = new LivingArcadeSimulation(Quiet(), Hero("returning"));
        Advance(returning, 1.15);
        var returningShot = returning.Snapshot.Shots.MaxBy(s => s.Age)!;
        Assert.IsTrue(returningShot.Returning);
        Assert.IsTrue(returningShot.Y < .2, "A returning projectile reaches the upper combat area before turning.");
        Advance(returning, .15);
        Assert.IsTrue(returning.Snapshot.Shots.MaxBy(s => s.Age)!.Y > returningShot.Y);
        Advance(returning, 3);
        Assert.IsTrue(returning.Snapshot.Shots.All(s => s.Age < 2.4), "Returned projectiles are removed on arrival.");
        Assert.AreEqual(2, returning.ResultHero.ActiveBossReward!.RemainingLevels, "Time and projectiles cannot consume stage duration.");
    }

    [TestMethod]
    public void PierceHitsTheSecondEnemyInsteadOfOnlyChangingTheDisplay()
    {
        var ordinary = new LivingArcadeSimulation(Targets(2, 1.1), Hero());
        var piercing = new LivingArcadeSimulation(Targets(2, 1.1), Hero(pierce: 1));
        for (var i = 0; i < 120 && piercing.ResultHero.TotalKills == 0; i++)
        { ordinary.Advance(1d / 60, default); piercing.Advance(1d / 60, default); }
        Assert.AreEqual(2L, piercing.ResultHero.TotalKills);
        Assert.AreEqual(1L, ordinary.ResultHero.TotalKills);
        Assert.AreEqual(0, piercing.Snapshot.Enemies.Count);
        Assert.AreEqual(1, ordinary.Snapshot.Enemies.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RealDefeatDoesNotConsumeRewardButCompletedStagesDo(int duration)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-reward-simulation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(root); var created = store.Create("Reward lifespan", 112);
            var run = store.Update(created.Id, value =>
            {
                value.CurrentLevel = 4; value.EntryState = Hero(duration: duration) with { MaxHealth = 1 };
                value.Levels[4] = Quiet();
            });
            var danger = Quiet() with { MiniEvents = [new() { Kind = "meteor", At = 0, X = .5 }] };
            var defeated = new LivingArcadeSimulation(danger, run.EntryState);
            Advance(defeated, 6);
            Assert.IsTrue(defeated.Snapshot.IsDefeated);
            store.RecordDefeat(run.Id, defeated.Snapshot.Progress, defeated.ResultHero);
            run = new LivingArcadeRunStore(root).Load(run.Id)!;
            Assert.AreEqual(duration, run.EntryState.ActiveBossReward!.RemainingLevels);
            Assert.AreEqual(duration, new LivingArcadeSimulation(Quiet(), run.EntryState).Snapshot.BossReward!.RemainingLevels);

            for (var stage = 0; stage < duration; stage++)
            {
                var number = run.CurrentLevel;
                var level = Quiet(number) with
                {
                    DurationSeconds = 30,
                    Waves = [new() { At = 0, Count = 1, X = .9 }],
                    EnemyTypes = [new() { Speed = .2, States = [new() { ForwardSpeed = 3, Duration = 10 }] }]
                };
                store.Update(run.Id, value => value.Levels[number] = level);
                store.TrySaveLevel(run.Id, Quiet(number + 1));
                var simulation = new LivingArcadeSimulation(level, run.EntryState);
                Advance(simulation, 35);
                Assert.IsTrue(simulation.Snapshot.IsComplete);
                store.CompleteLevel(run.Id, simulation.ResultHero, simulation.Snapshot);
                Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var advanced));
                run = new LivingArcadeRunStore(root).Load(advanced!.Id)!;
                Assert.AreEqual(duration - stage - 1, run.EntryState.ActiveBossReward?.RemainingLevels ?? 0);
            }
            Assert.IsNull(run.EntryState.ActiveBossReward);
            Assert.IsNull(new LivingArcadeSimulation(Quiet(run.CurrentLevel), run.EntryState).Snapshot.BossReward);
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("lopata-reward-simulation-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }
}
