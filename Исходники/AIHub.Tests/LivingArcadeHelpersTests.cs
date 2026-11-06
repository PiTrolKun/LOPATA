using AIHub.Models;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using System.Collections;
using System.Reflection;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeHelpersTests
{
    private static LivingArcadeLevel Quiet(params LivingArcadeMiniEvent[] events) =>
        LivingArcadeLevelPolicy.CreateFirst(104) with
        {
            DurationSeconds = 300, Waves = [new() { At = 250, Count = 1, X = .8 }],
            MiniEvents = events.ToList()
        };
    private static LivingArcadeHeroState Accompanied(double health = 24) => new()
    {
        Companions = [new() { Id = "original", Kind = "drone", Health = health, MaxHealth = 24 }]
    };
    private static void Advance(LivingArcadeSimulation simulation, double seconds)
    { for (var tick = 0; tick < (int)Math.Round(seconds * 60); tick++) simulation.Advance(1d / 60, default); }

    [TestMethod]
    public void HelpersHaveIndependentHealthAndShootWithoutThePlayerWeapon()
    {
        var simulation = new LivingArcadeSimulation(Quiet(), Accompanied());
        var alone = new LivingArcadeSimulation(Quiet(), new());
        Advance(simulation, .5);
        Advance(alone, .5);
        var helper = simulation.Snapshot.Companions.Single();
        Assert.AreEqual(1, helper.HealthFraction);
        Assert.IsFalse(helper.Hostile);
        Assert.IsTrue(helper.X < simulation.Snapshot.PlayerX);
        Assert.IsTrue(simulation.Snapshot.Shots.Count > alone.Snapshot.Shots.Count);
        Assert.AreEqual(100, simulation.Snapshot.Health);
    }

    [TestMethod]
    public void AHostileProjectileHurtsTheHelperAndItsRemainingHealthSurvivesTheNextSession()
    {
        var source = Accompanied();
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent
            { At = 0, Kind = "meteor", X = .415 }), source);
        Advance(simulation, 4);
        Assert.AreEqual(100, simulation.Snapshot.Health, 1e-8);
        Assert.AreEqual(.5, simulation.Snapshot.Companions.Single().HealthFraction, 1e-8);
        Assert.AreEqual(12, simulation.ResultHero.Companions.Single().Health, 1e-8);
        Assert.AreEqual(24, source.Companions.Single().Health,
            "The source save must not be mutated through the simulation's companion list.");
        var resumed = new LivingArcadeSimulation(Quiet(), simulation.ResultHero);
        Assert.AreEqual(.5, resumed.Snapshot.Companions.Single().HealthFraction, 1e-8);
        var copy = simulation.ResultHero;
        copy.Companions.Single().Health = 1;
        Assert.AreEqual(12, simulation.ResultHero.Companions.Single().Health, 1e-8);
    }

    [TestMethod]
    public void ADeadHelperDoesNotReturnOnRestartOrFromRepair()
    {
        var simulation = new LivingArcadeSimulation(Quiet(new LivingArcadeMiniEvent
            { At = 0, Kind = "meteor", X = .415 }, new LivingArcadeMiniEvent
            { At = 1, X = .5, BonusKind = "repair" }), Accompanied(12));
        Advance(simulation, 10);
        Assert.AreEqual(0, simulation.Snapshot.Companions.Count);
        Assert.AreEqual(0, simulation.ResultHero.Companions.Count);
        var resumed = new LivingArcadeSimulation(Quiet(), simulation.ResultHero);
        Assert.AreEqual(0, resumed.Snapshot.Companions.Count);
    }

    [TestMethod]
    public void RareHelperPickupSummonsOneLifeAndNeverExceedsTwoCompanions()
    {
        var simulation = new LivingArcadeSimulation(Quiet(Enumerable.Range(0, 3).Select(index =>
            new LivingArcadeMiniEvent { At = index, X = .5, BonusKind = "helper" }).ToArray()), new());
        for (var tick = 0; tick < 900 && simulation.Snapshot.BonusCount == 0; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1, simulation.Snapshot.BonusCount);
        Assert.IsTrue(simulation.Snapshot.RecentBonus!.CompanionAdded);
        for (var tick = 0; tick < 900 && simulation.Snapshot.BonusCount < 3; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(3, simulation.Snapshot.BonusCount);
        Assert.AreEqual(2, simulation.Snapshot.Companions.Count);
        Assert.AreEqual(2, simulation.ResultHero.Companions.Count);
        Assert.AreEqual(2, simulation.ResultHero.Companions.Select(c => c.Id).Distinct().Count());
        Assert.IsTrue(simulation.ResultHero.Companions.All(c => c.Health == 24 && c.MaxHealth == 24));
        // The last pickup hit the two-helper limit and must not claim that a third life was granted.
        var receipt = simulation.Snapshot.RecentBonus!;
        Assert.IsFalse(receipt.CompanionAdded);
        foreach (var language in new[] { "ru", "en" })
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var text = LivingArcadeHud.Pickup(localizer.T, receipt);
            StringAssert.Contains(text, localizer.T("Arcade.Bonus.Maximum"));
            Assert.IsFalse(text.Contains(localizer.T("Arcade.Companion.OneLife")));
            var added = LivingArcadeHud.Pickup(localizer.T, receipt with { CompanionAdded = true });
            StringAssert.Contains(added, localizer.T("Arcade.Companion.OneLife"));
            Assert.IsFalse(added.Contains(localizer.T("Arcade.Bonus.Maximum")));
        }
    }

    [TestMethod]
    public void HelperContactConsumesItsHealthWithoutAwardingAnEnemyKill()
    {
        var level = Quiet() with
        {
            EnemyTypes = [new() { Id = "heavy", Health = 200, Speed = .2 }],
            Waves = [new() { At = 0, X = .415, Count = 1, EnemyId = "heavy" }]
        };
        var simulation = new LivingArcadeSimulation(level, Accompanied());
        // Isolate a body collision rather than letting the helper shoot down a now-killable approaching enemy.
        var helpers = (IList)typeof(LivingArcadeSimulation)
            .GetField("_helpers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(simulation)!;
        helpers[0]!.GetType().GetField("FireClock")!.SetValue(helpers[0], 100d);
        typeof(LivingArcadeSimulation).GetField("_fireClock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(simulation, 100d);
        Advance(simulation, 5);
        Assert.AreEqual(10, simulation.ResultHero.Companions.Single().Health, 1e-8);
        Assert.AreEqual(100, simulation.Snapshot.Health, 1e-8);
        Assert.AreEqual(0L, simulation.ResultHero.TotalKills);
    }
}
