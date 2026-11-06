using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeExperienceViewTests
{
    [TestMethod]
    public void ActualViewsDistinguishAnOrdinaryEnemyAFutureShooterAndTheBoss()
    {
        var level = new LivingArcadeLevel
        {
            Number = 3, Authored = true, DurationSeconds = 30, MiniEvents = [],
            EnemyTypes =
            [
                new() { Id = "ordinary", Health = 10, Speed = .02,
                    States = [new() { NextState = "advance" }, new() { Id = "unreachable", ShotInterval = 1 }] },
                new() { Id = "active", Health = 10, Speed = .02,
                    States = [new() { Duration = 20, NextState = "fire" },
                        new() { Id = "fire", ShotInterval = 2, NextState = "advance" }] }
            ],
            Waves = [new() { At = 0, Count = 1, EnemyId = "ordinary", X = .85 },
                new() { At = 0, Count = 1, EnemyId = "active", X = .15 }]
        };
        var simulation = new LivingArcadeSimulation(level, new());
        simulation.Advance(1d / 60, default);
        var ordinary = simulation.Snapshot.Enemies.Single(e => e.Id.StartsWith("ordinary-", StringComparison.Ordinal));
        var active = simulation.Snapshot.Enemies.Single(e => e.Id.StartsWith("active-", StringComparison.Ordinal));
        Assert.AreEqual(1, ordinary.ExperienceValue, "Unreachable shooting states do not upgrade an ordinary enemy.");
        Assert.AreEqual(5, active.ExperienceValue, "A reachable shooting phase counts even before it starts firing.");
        for (var i = 0; i < 240 && simulation.Snapshot.BossPhase == 0 && !simulation.Snapshot.IsDefeated; i++)
            simulation.Advance(.25, default);
        Assert.AreEqual(30, simulation.Snapshot.Enemies.Single(e => e.IsBoss).ExperienceValue);
        Assert.IsFalse(simulation.Snapshot.IsDefeated);
    }
}
