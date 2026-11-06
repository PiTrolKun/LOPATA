using AIHub.Models;
using AIHub.Services.LivingArcade;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeTimeBonusTests
{
    private static LivingArcadeLevel Level(bool selective = false, bool bonuses = true) =>
        LivingArcadeLevelPolicy.CreateFirst(17) with { DurationSeconds = 300, SelectiveTime = selective,
            Waves = [new() { At = 250, Count = 1 }], MiniEvents = bonuses
                ? [new() { At = 0, X = .5, BonusKind = "repair" },
                    new() { At = 0, X = .5, BonusKind = "time_scope" }] : [] };
    private static void Advance(LivingArcadeSimulation simulation, double seconds, LivingArcadeInput input = default)
    { for (var i = 0; i < (int)Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, input); }

    [TestMethod]
    public void BlueTimeBonusIsVisibleBeforeCollectionAndTogglesTheInitialScope()
    {
        var simulation = new LivingArcadeSimulation(Level(), new());
        simulation.Advance(1d / 60, default);
        Assert.IsFalse(simulation.Snapshot.SelectiveTime);
        var time = simulation.Snapshot.Pickups.Single(p => p.Id == "bonus_time_scope");
        var ordinary = simulation.Snapshot.Pickups.Single(p => p.Id == "bonus_repair");
        Assert.AreNotEqual(time.Color, ordinary.Color); Assert.AreNotEqual(time.Id, ordinary.Id);
        Advance(simulation, 8);
        Assert.IsTrue(simulation.Snapshot.SelectiveTime); Assert.AreEqual(1, simulation.Snapshot.TimeScopeBonusCount);
        var initiallySelective = new LivingArcadeSimulation(Level(selective: true), new());
        Assert.IsTrue(initiallySelective.Snapshot.SelectiveTime);
        Advance(initiallySelective, 8);
        Assert.IsFalse(initiallySelective.Snapshot.SelectiveTime);
    }

    [TestMethod]
    public void ScopeBonusChangesOwnSpeedButNeverTheRealEffectCooldownOrPauseClocks()
    {
        var world = new LivingArcadeSimulation(Level(bonuses: false), new());
        var selective = new LivingArcadeSimulation(Level(), new());
        Advance(selective, 8);
        Assert.IsTrue(selective.Snapshot.SelectiveTime);
        var start = selective.Snapshot.Progress;
        Advance(world, .25, new(Right: true, SpeedUp: true));
        Advance(selective, .25, new(Right: true, SpeedUp: true));
        Assert.AreEqual(.5 + .46 * .375, world.Snapshot.PlayerX, 1e-7);
        Assert.AreEqual(.5 + .46 * .25, selective.Snapshot.PlayerX, 1e-7);
        Assert.AreEqual(.375, selective.Snapshot.Progress - start, 1e-7);
        Assert.AreEqual(world.Snapshot.SpeedUpCooldown, selective.Snapshot.SpeedUpCooldown, 1e-7);
        Assert.AreEqual(world.Snapshot.EffectSeconds, selective.Snapshot.EffectSeconds, 1e-7);
        Assert.AreEqual("speed_up", selective.Snapshot.TimeEffect);
        var paused = selective.Snapshot;
        selective.SetPaused(true); Advance(selective, 10);
        Assert.AreEqual(paused.Progress, selective.Snapshot.Progress);
        Assert.AreEqual(paused.EffectSeconds, selective.Snapshot.EffectSeconds);
        Assert.AreEqual(paused.SpeedUpCooldown, selective.Snapshot.SpeedUpCooldown);
        selective.SetPaused(false); Advance(selective, .25, new(SlowDown: true));
        Assert.AreEqual("slow_down", selective.Snapshot.TimeEffect);
        Assert.AreEqual(15 - 14d / 60, selective.Snapshot.EffectSeconds, .001);
        Assert.AreEqual(120 - 14d / 60, selective.Snapshot.SlowDownCooldown, .001);
    }

    [TestMethod]
    public void ScopeCanChangeMidEffectAndFogDoesNotHideTheActiveTimeEffectFromTheHud()
    {
        var level = Level() with { MiniEvents = [new() { At = 0, X = .5, BonusKind = "repair" },
            new() { At = 0, X = .5, BonusKind = "time_scope" },
            new() { At = 8, Kind = "fog" }] };
        var simulation = new LivingArcadeSimulation(level, new());
        Advance(simulation, 6); Advance(simulation, 1.5, new(SpeedUp: true));
        Assert.IsTrue(simulation.Snapshot.SelectiveTime);
        Assert.AreEqual("fog", simulation.Snapshot.ActiveEffect);
        Assert.AreEqual("speed_up", simulation.Snapshot.TimeEffect);
        Assert.IsTrue(simulation.Snapshot.EffectSeconds > 13 && simulation.Snapshot.EffectSeconds < 14);
    }
}
