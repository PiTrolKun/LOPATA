using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeResearchMechanicsTests
{
    private static LivingArcadeLevel Level(LivingArcadeEnemyDefinition enemy, double x = .5) => new()
    {
        DurationSeconds = 30, EnemyTypes = [enemy], MiniEvents = [],
        Waves = [new() { At = 0, EnemyId = enemy.Id, Count = 1, X = x, SpawnInterval = 0 }]
    };

    private static void Advance(LivingArcadeSimulation simulation, double seconds, LivingArcadeInput input = default)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, input);
    }

    private static LivingArcadeEnemyDefinition ShieldEnemy(double rotation = 0, double opening = 0) => new()
    {
        Id = "shield_test", Health = 100, Speed = .2,
        States = [new() { Duration = 1, Shield = true, NextState = "guard" },
            new() { Id = "guard", Duration = 10, NextState = "guard", Motion = "hold", ShieldMode = "rotating",
                WeakSpotAngleDegrees = opening, WeakSpotWidthDegrees = 60, ShieldRotationDegreesPerSecond = rotation }]
    };

    [TestMethod]
    public void RotatingOpeningActuallyAcceptsDamageOnlyDuringTheExposedAngularWindow()
    {
        var closed = new LivingArcadeSimulation(Level(ShieldEnemy()), new());
        var rotating = new LivingArcadeSimulation(Level(ShieldEnemy(90)), new());
        Advance(closed, 1.3); Advance(rotating, 1.3);
        Assert.AreEqual(1, closed.Snapshot.Enemies.Single().HealthFraction, 1e-8);
        Assert.AreEqual(1, rotating.Snapshot.Enemies.Single().HealthFraction, 1e-8);
        Assert.AreEqual(117, rotating.Snapshot.Enemies.Single().WeakSpotAngleDegrees, .001);
        Advance(closed, 1); Advance(rotating, 1);
        Assert.AreEqual(1, closed.Snapshot.Enemies.Single().HealthFraction, 1e-8);
        Assert.IsTrue(rotating.Snapshot.Enemies.Single().HealthFraction < .99,
            "Real friendly projectiles must damage the lower opening as it rotates past the player.");
        var health = rotating.Snapshot.Enemies.Single().HealthFraction;
        Advance(rotating, 1);
        Assert.AreEqual(health, rotating.Snapshot.Enemies.Single().HealthFraction, 1e-8,
            "Once the opening has rotated away, the same shot stream must be blocked again.");
    }

    [TestMethod]
    public void WarningPhaseHoldsPositionThenPerformsTheDeclaredDashAndRecovers()
    {
        var enemy = new LivingArcadeEnemyDefinition { Id = "dasher", Health = 100, Speed = .2,
            States = [new() { Duration = .6, NextState = "warning" },
                new() { Id = "warning", Duration = .6, NextState = "dash", Motion = "hold", Telegraph = true },
                new() { Id = "dash", Duration = .5, NextState = "recover", Motion = "dash", ForwardSpeed = 3, HorizontalSpeed = .2 },
                new() { Id = "recover", Duration = 2, NextState = "warning", ForwardSpeed = .2 }] };
        var simulation = new LivingArcadeSimulation(Level(enemy, .75), new());
        Advance(simulation, .7); var warning = simulation.Snapshot.Enemies.Single();
        Assert.IsTrue(warning.Telegraph);
        Advance(simulation, .4); Assert.AreEqual(warning.Y, simulation.Snapshot.Enemies.Single().Y, 1e-8);
        Advance(simulation, .3); var dash = simulation.Snapshot.Enemies.Single();
        Assert.IsFalse(dash.Telegraph); Assert.IsTrue(dash.Y > warning.Y + .1); Assert.IsTrue(dash.X > warning.X + .03);
        Advance(simulation, .5); var recovered = simulation.Snapshot.Enemies.Single();
        Advance(simulation, .3);
        Assert.IsTrue(simulation.Snapshot.Enemies.Single().Y - recovered.Y < .02);
    }

    [TestMethod]
    public void ReturningVolleyMovesBackToTheEmitterAndIsRemovedWhenItArrives()
    {
        var enemy = new LivingArcadeEnemyDefinition { Id = "boomerang", Health = 100, Speed = .2,
            States = [new() { Duration = 1, NextState = "shoot" },
                new() { Id = "shoot", Motion = "hold", Duration = 1, NextState = "wait", ShotInterval = 20,
                    ShotCount = 3, ShotSpread = 40, ShotSpeed = .25, ShotReturnAfterSeconds = .4 },
                new() { Id = "wait", Motion = "hold", Duration = 10, NextState = "wait" }] };
        var simulation = new LivingArcadeSimulation(Level(enemy, .8), new());
        Advance(simulation, 1.3);
        var outward = simulation.Snapshot.Shots.Where(s => s.Hostile).OrderBy(s => s.X).ToArray();
        Assert.HasCount(3, outward); Assert.IsTrue(outward.All(s => !s.Returning));
        Advance(simulation, .25);
        var returning = simulation.Snapshot.Shots.Where(s => s.Hostile).OrderBy(s => s.X).ToArray();
        Assert.HasCount(3, returning); Assert.IsTrue(returning.All(s => s.Returning));
        var before = returning.Select(s => Math.Abs(s.Y - simulation.Snapshot.Enemies.Single().Y)).Max();
        Advance(simulation, .1);
        Assert.IsTrue(simulation.Snapshot.Shots.Where(s => s.Hostile)
            .Select(s => Math.Abs(s.Y - simulation.Snapshot.Enemies.Single().Y)).Max() < before);
        Advance(simulation, 1);
        Assert.AreEqual(0, simulation.Snapshot.Shots.Count(s => s.Hostile));
    }

    [TestMethod]
    public void AVolleyWhoseEmitterWasDestroyedReturnsToItsRecordedLaunchPoint()
    {
        var enemy = new LivingArcadeEnemyDefinition { Id = "fragile_emitter", Health = .25, Speed = .2,
            States = [new() { Duration = 1.5, Shield = true, NextState = "fire" },
                new() { Id = "fire", Duration = 10, NextState = "fire", Motion = "hold", ShotInterval = 20,
                    ShotReturnAfterSeconds = .4 }] };
        var simulation = new LivingArcadeSimulation(Level(enemy), new());
        Advance(simulation, 1.5);
        // A shot consumed by the last shield tick stays consumed; wait for the next real hit.
        for (var tick = 0; tick < 30 && simulation.Snapshot.Enemies.Count > 0; tick++) Advance(simulation, 1d / 60);
        Assert.AreEqual(0, simulation.Snapshot.Enemies.Count,
            "The player must have killed the emitter after it released its first shot.");
        Assert.AreEqual(1, simulation.Snapshot.Shots.Count(s => s.Hostile));
        Advance(simulation, .45);
        Assert.IsTrue(simulation.Snapshot.Shots.Single(s => s.Hostile).Returning);
        Advance(simulation, .8);
        Assert.AreEqual(0, simulation.Snapshot.Shots.Count(s => s.Hostile));
        Assert.IsTrue(simulation.Snapshot.Shots.All(s => double.IsFinite(s.X) && double.IsFinite(s.Y)));
    }

    [TestMethod]
    public void NewMechanicsAreRejectedWhenMalformedRatherThanSilentlyNormalized()
    {
        var enemy = ShieldEnemy(); enemy.States[1].WeakSpotWidthDegrees = 0;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
        enemy = ShieldEnemy(); enemy.States[1].Shield = true;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
        enemy = ShieldEnemy(); enemy.States[1].ShotReturnAfterSeconds = .1;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
        enemy = ShieldEnemy(); enemy.States[1].Motion = "dash"; enemy.States[1].Duration = 1; enemy.States[1].ForwardSpeed = 2;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
        enemy = ShieldEnemy(); enemy.States[1].ShieldRotationDegreesPerSecond = double.NaN;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
    }

    [TestMethod]
    public void OldSerializedLevelsStillUseTheirOriginalShieldAndStraightProjectiles()
    {
        var json = JsonSerializer.Serialize(LivingArcadeLevelPolicy.CreateFirst(21), LivingArcadeLevelPolicy.JsonOptions);
        var old = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var enemy in old["enemyTypes"]!.AsArray())
        {
            enemy!.AsObject().Remove("visual"); enemy.AsObject().Remove("encounterClass");
            foreach (var state in enemy["states"]!.AsArray())
                foreach (var key in new[] { "shieldMode", "weakSpotAngleDegrees", "weakSpotWidthDegrees",
                    "shieldRotationDegreesPerSecond", "telegraph", "shotReturnAfterSeconds" }) state!.AsObject().Remove(key);
        }
        var parsed = LivingArcadeLevelPolicy.Parse(old.ToJsonString());
        Assert.IsTrue(parsed.EnemyTypes.All(e => e.Visual is null && e.EncounterClass == "normal"));
        Assert.IsTrue(parsed.EnemyTypes.SelectMany(e => e.States).All(s => s.ShieldMode == "legacy" && s.ShotReturnAfterSeconds == 0));
        var simulation = new LivingArcadeSimulation(parsed, new()); Advance(simulation, 30);
        Assert.IsTrue(simulation.Snapshot.Shots.All(s => !s.Returning));
    }

    [TestMethod]
    public void LargeEncountersHaveASeparateRootSpawnBudgetAndReturnVolleysEnterTheCostProbe()
    {
        var enemy = ShieldEnemy(); enemy.EncounterClass = "rare-large"; enemy.Radius = .2;
        var level = Level(enemy); LivingArcadeLevelPolicy.Validate(level);
        level.Waves.Add(new() { At = 20, EnemyId = enemy.Id, Count = 1 });
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level));
        enemy.EncounterClass = "normal";
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(Level(enemy)));
        enemy = ShieldEnemy(); enemy.States[1].ShotInterval = .4; enemy.States[1].ShotCount = 8; enemy.States[1].ShotReturnAfterSeconds = 3;
        level = Level(enemy); level.Waves[0].Count = 3; level.Waves[0].SpawnInterval = 0;
        var result = LivingArcadeLevelPolicy.Probe(level);
        Assert.IsFalse(result.Accepted); Assert.AreEqual("LivingArcade.LevelCostExceeded", result.RejectionKey);
        Assert.AreEqual(720, result.EstimatedPeakHostileShots, .001);
        Assert.AreEqual(0, result.SimulationTicks);
    }
}
