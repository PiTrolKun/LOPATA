using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeActiveEnemyTests
{
    private static LivingArcadeLevel Dense(int number = 7) => LivingArcadeLevelPolicy.CreateFirst(17) with
    {
        Number = number, Authored = false, MiniEvents = [],
        EnemyTypes = [new() { Id = "lead", Health = 50, Speed = .04,
            States = [new() { ShotInterval = 5, ShotCount = 2 }] },
            new() { Id = "support", Health = 20, Speed = .06,
                States = [new() { ForwardSpeed = .75 }] }],
        Waves = Enumerable.Range(0, 38).Select(i => new LivingArcadeWave
        { At = 2 + i * 3, EnemyId = i % 6 == 0 ? "lead" : "support", Count = i % 6 == 0 ? 1 : 3,
            X = .25 + i % 3 * .25, Spacing = .06, SpawnInterval = .25 }).ToList()
    };

    [TestMethod]
    public void DenseSavedPlansGainAttackersWithoutLosingOrdinaryUnitsOrSourceArtwork()
    {
        var source = Dense();
        var before = JsonSerializer.Serialize(source, LivingArcadeLevelPolicy.JsonOptions);
        var played = LivingArcadePacing.ForPlay(source);
        var raider = played.EnemyTypes.Single(e => e.Id == "active_support");
        Assert.IsTrue(raider.States.All(s => s.ShotInterval >= 6 && s.ShotCount == 1));
        Assert.AreEqual(12, raider.Health);
        Assert.AreEqual(source.Waves.Sum(w => w.Count), played.Waves.Sum(w => w.Count));
        Assert.IsTrue(played.Waves.Where(w => w.EnemyId == "support").Sum(w => w.Count) > 40);
        Assert.IsTrue(played.Waves.Where(w => w.EnemyId == "active_support").Sum(w => w.Count) >= 8);
        Assert.AreEqual(before, JsonSerializer.Serialize(source, LivingArcadeLevelPolicy.JsonOptions));
        Assert.AreEqual(JsonSerializer.Serialize(played, LivingArcadeLevelPolicy.JsonOptions),
            JsonSerializer.Serialize(LivingArcadePacing.ForPlay(played), LivingArcadeLevelPolicy.JsonOptions));
        var cost = LivingArcadeLevelPolicy.EstimateCost(played);
        Assert.IsTrue(cost.PeakScheduledEnemies <= 80 && cost.EstimatedPeakHostileShots <= 480);
    }

    [TestMethod]
    public void SeveralAttackersCanBeAliveAndFireTogetherWhileTutorialStaysAuthored()
    {
        var tutorial = LivingArcadeLevelPolicy.CreateFirst(17);
        Assert.AreSame(tutorial, LivingArcadePacing.ForPlay(tutorial));
        var simulation = new LivingArcadeSimulation(LivingArcadePacing.ForPlay(Dense()), new());
        var activeIds = simulation.EnemyDefinitions.Where(e => e.States.Any(s => s.ShotInterval > 0))
            .Select(e => e.Id).ToArray();
        var peak = 0; var hostilePeak = 0;
        while (!simulation.Snapshot.IsComplete && !simulation.Snapshot.IsDefeated && simulation.Snapshot.Progress < 100)
        {
            simulation.Advance(.25, new(Right: Math.Sin(simulation.Snapshot.Progress) > 0,
                Left: Math.Sin(simulation.Snapshot.Progress) <= 0));
            peak = Math.Max(peak, simulation.Snapshot.Enemies.Count(e => e.Y > 0 && e.Y < 1
                && activeIds.Any(id => e.Id.StartsWith(id + "-", StringComparison.Ordinal))));
            hostilePeak = Math.Max(hostilePeak, simulation.Snapshot.Shots.Count(s => s.Hostile));
        }
        Assert.IsTrue(peak >= 3, $"Visible attackers peak: {peak}");
        Assert.IsTrue(hostilePeak >= 3, $"Hostile projectiles peak: {hostilePeak}");
    }

    [TestMethod]
    public void MaxedSourceArrayIsNotExpandedBeyondTheLevelContract()
    {
        var level = Dense() with { Waves = Enumerable.Range(0, 120).Select(i => new LivingArcadeWave
            { At = i, Count = 1, EnemyId = "support", X = .5 }).ToList() };
        var played = LivingArcadePacing.ForPlay(level);
        LivingArcadeLevelPolicy.Validate(played);
        Assert.IsTrue(played.Waves.Count <= 120);
        Assert.AreEqual(120, played.Waves.Sum(w => w.Count));
    }
}
