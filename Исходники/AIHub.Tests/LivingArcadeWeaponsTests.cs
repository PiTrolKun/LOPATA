using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeWeaponsTests
{
    private static LivingArcadeLevel Quiet(params LivingArcadeMiniEvent[] events) =>
        LivingArcadeLevelPolicy.CreateFirst(89) with
        {
            DurationSeconds = 300, Waves = [new() { At = 250, Count = 1, X = .8 }],
            MiniEvents = events.ToList()
        };

    private static void Advance(LivingArcadeSimulation simulation, double seconds, LivingArcadeInput input = default)
    { for (var tick = 0; tick < (int)Math.Round(seconds * 60); tick++) simulation.Advance(1d / 60, input); }

    private static void Collect(LivingArcadeSimulation simulation)
    {
        for (var tick = 0; tick < 900 && simulation.Snapshot.BonusCount == 0; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1, simulation.Snapshot.BonusCount);
    }

    [TestMethod]
    [DataRow("pistol", 1, 5d, 1d)]
    [DataRow("shotgun", 3, 2.5d, .6d)]
    [DataRow("smg", 1, 9d, .5d)]
    public void OrdinaryWeaponsHaveDistinctRealVolleyAndComparableDamage(string kind, int count, double rate, double damage)
    {
        var simulation = new LivingArcadeSimulation(Quiet(), new() { WeaponKind = kind });
        simulation.Advance(1d / 60, default);
        Assert.AreEqual(kind, simulation.Snapshot.WeaponKind);
        Assert.AreEqual(count, simulation.Snapshot.Shots.Count(s => !s.Hostile));
        Assert.AreEqual(count, simulation.Snapshot.SpreadShots);
        Assert.AreEqual(rate, simulation.Snapshot.FireRate, 1e-8);
        Assert.AreEqual(damage, simulation.Snapshot.ProjectileDamage, 1e-8);
        Assert.IsTrue(count * rate * damage is >= 4.4 and <= 5.1,
            "A different permanent weapon must not be a hidden large permanent damage multiplier.");
        var resumed = new LivingArcadeSimulation(Quiet(), simulation.ResultHero);
        Assert.AreEqual(kind, resumed.Snapshot.WeaponKind);
    }

    [TestMethod]
    public void OrdinaryWeaponPickupReplacesThePreviousWeaponUntilAnotherPickup()
    {
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent
            { At = 0, X = .5, BonusKind = "weapon_shotgun" }), new() { WeaponKind = "smg" });
        Collect(simulation);
        Assert.AreEqual("shotgun", simulation.Snapshot.WeaponKind);
        Assert.AreEqual("shotgun", simulation.ResultHero.WeaponKind);
        Advance(simulation, 12);
        Assert.AreEqual("shotgun", simulation.Snapshot.WeaponKind);
        Assert.AreEqual(0, simulation.Snapshot.WeaponSeconds);
    }

    [TestMethod]
    [DataRow("weapon_laser", "laser")]
    [DataRow("weapon_rocket", "rockets")]
    public void HeavyWeaponIsTemporaryAndReturnsToSavedOrdinaryWeapon(string pickup, string weapon)
    {
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent
            { At = 0, X = .5, BonusKind = pickup }), new() { WeaponKind = "smg" });
        Collect(simulation);
        Assert.AreEqual(weapon, simulation.Snapshot.WeaponKind);
        Assert.IsTrue(simulation.Snapshot.WeaponSeconds is > 0 and <= 8);
        Assert.AreEqual("smg", simulation.ResultHero.WeaponKind);
        simulation.SetPaused(true); Advance(simulation, 12);
        Assert.AreEqual(weapon, simulation.Snapshot.WeaponKind);
        simulation.SetPaused(false); Advance(simulation, 8.1, new(SlowDown: true));
        Assert.AreEqual("smg", simulation.Snapshot.WeaponKind);
        Assert.AreEqual(0, simulation.Snapshot.WeaponSeconds);
    }

    [TestMethod]
    [DataRow("weapon_laser", .25)]
    [DataRow("weapon_rocket", 1.5)]
    public void HeavyWeaponCanDestroySeveralTargetsWithTheSameRealProjectile(string weapon, double targetHealth)
    {
        var level = Quiet(new LivingArcadeMiniEvent { At = 0, X = .5, BonusKind = weapon }) with
        {
            EnemyTypes = [new() { Id = "target", Health = targetHealth, Speed = .015 }],
            Waves = Enumerable.Range(0, 4).Select(_ => new LivingArcadeWave
                { At = 9, Count = 1, X = .5, EnemyId = "target" }).ToList()
        };
        var simulation = new LivingArcadeSimulation(level, new());
        Collect(simulation);
        long largestGain = 0;
        for (var tick = 0; tick < 240 && simulation.ResultHero.TotalKills < 4; tick++)
        {
            var before = simulation.ResultHero.TotalKills;
            simulation.Advance(1d / 60, default);
            largestGain = Math.Max(largestGain, simulation.ResultHero.TotalKills - before);
        }
        Assert.AreEqual(4L, largestGain);
        Assert.AreEqual(4L, simulation.ResultHero.TotalKills);
    }
}
