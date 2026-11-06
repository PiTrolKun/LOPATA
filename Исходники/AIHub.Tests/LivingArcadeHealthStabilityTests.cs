using System.Collections;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeHealthStabilityTests
{
    public TestContext TestContext { get; set; } = null!;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static LivingArcadeHeroState SavedHero(string weapon = "pistol") => new()
    {
        PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
        TotalKills = 252, PermanentPower = 1.01, WeaponPower = 1.1954376428810503,
        MaxHealth = 108, FireRate = 5.861750571524201, WeaponKind = weapon
    };

    private static LivingArcadeEnemyDefinition Definition(string scenario, double health = 200) => scenario switch
    {
        "hold-dash" => new()
        {
            Id = "target", Health = health, Speed = .2, Radius = .015, InitialState = "warning",
            States = [new() { Id = "warning", Motion = "hold", Duration = .6, Telegraph = true, NextState = "dash" },
                new() { Id = "dash", Motion = "dash", Duration = 1.5, ForwardSpeed = 3, NextState = "warning" }]
        },
        "full-shield" => new()
        {
            Id = "target", Health = health, Speed = .2, Radius = .015, InitialState = "closed",
            States = [new() { Id = "closed", Duration = 2, ForwardSpeed = 3, ShieldMode = "full", NextState = "open", ShotInterval = 20 },
                new() { Id = "open", Duration = 2, ForwardSpeed = 3, ShieldMode = "off", NextState = "closed", ShotInterval = 20 }]
        },
        "rotating-shield" => new()
        {
            Id = "target", Health = health, Speed = .2, Radius = .015, InitialState = "advance",
            States = [new() { Id = "advance", Duration = 2, ForwardSpeed = 3, NextState = "advance", ShotInterval = 20,
                ShieldMode = "rotating", WeakSpotAngleDegrees = 180, WeakSpotWidthDegrees = 60, ShieldRotationDegreesPerSecond = 90 }]
        },
        "long-full-shield" => new()
        {
            Id = "target", Health = health, Speed = .06, Radius = .015, InitialState = "closed",
            States = [new() { Id = "closed", Duration = 3, ForwardSpeed = .75, ShieldMode = "full", NextState = "open" },
                new() { Id = "open", Duration = .6, ForwardSpeed = .75, ShieldMode = "off", NextState = "closed" }]
        },
        "long-rotating-shield" => new()
        {
            Id = "target", Health = health, Speed = .06, Radius = .015, InitialState = "advance",
            States = [new() { Id = "advance", Duration = 2, ForwardSpeed = .75, NextState = "advance",
                ShieldMode = "rotating", WeakSpotAngleDegrees = 180, WeakSpotWidthDegrees = 60, ShieldRotationDegreesPerSecond = 90 }]
        },
        _ => new()
        {
            Id = "target", Health = health, Speed = scenario == "non-contact" ? .015 : scenario == "slow" ? .06 : .2,
            Radius = scenario == "tiny-fast" ? .012 : .015, InitialState = "advance",
            States = [new() { Id = "advance", Duration = 2, NextState = "advance",
                ForwardSpeed = scenario == "non-contact" ? .25 : scenario == "slow" ? .75 : 3 }]
        }
    };

    private static LivingArcadeLevel Level(params LivingArcadeEnemyDefinition[] enemies) => new()
    {
        Number = 7, Authored = false, DurationSeconds = 300, SelectiveTime = true,
        EnemyTypes = enemies.ToList(),
        // Insert the fixture through the production birth method. This unrelated later wave keeps the level valid.
        Waves = [new() { At = 250, Count = 1, EnemyId = enemies[0].Id, X = .1 }], MiniEvents = []
    };

    private static void Set(LivingArcadeSimulation simulation, string field, object value) =>
        typeof(LivingArcadeSimulation).GetField(field, PrivateInstance)!.SetValue(simulation, value);

    private static object? Invoke(LivingArcadeSimulation simulation, string method, params object?[] arguments) =>
        typeof(LivingArcadeSimulation).GetMethod(method, PrivateInstance)!.Invoke(simulation, arguments);

    private static void SetEnemyY(LivingArcadeSimulation simulation, double y)
    {
        var enemies = (IList)typeof(LivingArcadeSimulation).GetField("_enemies", PrivateInstance)!.GetValue(simulation)!;
        var enemy = enemies[0]!;
        enemy.GetType().GetField("Y", BindingFlags.Instance | BindingFlags.Public)!.SetValue(enemy, y);
    }

    private sealed record Fight(double InitialHealth, int HitFrames, double Seconds, double DamageTaken,
        LivingArcadeSnapshot Snapshot, LivingArcadeHeroState Hero, IReadOnlyList<string> ObservedTimeModes);

    private static LivingArcadeInput TimeInput(string mode, int tick) => mode switch
    {
        "normal" => default,
        "slow-down" => new(SlowDown: tick == 0),
        "speed-up" => new(SpeedUp: tick == 0),
        "speed-then-slow" => new(SpeedUp: tick == 0, SlowDown: tick == 90),
        "slow-then-speed" => new(SlowDown: tick == 0, SpeedUp: tick == 90),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private Fight Destroy(LivingArcadeEnemyDefinition definition, string weapon, string powerup = "", double spawnY = -.03,
        bool selective = true, string timeMode = "speed-up")
    {
        var hero = SavedHero(weapon);
        var simulation = new LivingArcadeSimulation(Level(definition) with { SelectiveTime = selective }, hero, attempts: 2,
            deathProgress: 10000, penalty: "weak");
        Set(simulation, "_negative", "jam"); Set(simulation, "_negativeSeconds", 6d);
        if (powerup.Length > 0)
        { Set(simulation, "_powerup", powerup); Set(simulation, "_powerupSeconds", 8d); }
        Invoke(simulation, "SpawnEnemy", definition.Id, .5, spawnY);
        var initial = simulation.Snapshot.Enemies.Single().MaxHealth;
        Assert.IsTrue(initial > 0 && initial <= definition.Health);
        // Start immediately after a volley: a fresh shot cannot hide a bad approach budget.
        Set(simulation, "_fireClock", 1 / simulation.Snapshot.FireRate);
        var lastHealth = initial;
        var hitFrames = 0;
        var observedTimeModes = new HashSet<string>(StringComparer.Ordinal);
        for (var tick = 0; tick < 50 * 60; tick++)
        {
            simulation.Advance(1d / 60, TimeInput(timeMode, tick));
            var snapshot = simulation.Snapshot;
            observedTimeModes.Add(snapshot.TimeEffect.Length > 0 ? snapshot.TimeEffect : "normal");
            if (snapshot.Enemies.Count > 0)
            {
                var health = snapshot.Enemies.Single().Health;
                if (health < lastHealth - 1e-10) hitFrames++;
                lastHealth = health;
            }
            if (simulation.ResultHero.TotalKills > hero.TotalKills)
            {
                var result = new Fight(initial, hitFrames + 1, (tick + 1) / 60d,
                    snapshot.DamageTaken, snapshot, simulation.ResultHero, observedTimeModes.ToArray());
                TestContext.WriteLine(JsonSerializer.Serialize(new
                {
                    scenario = definition.Id + ":" + definition.States[0].Motion + ":" + definition.States[0].ShieldMode,
                    weapon, powerup, spawnY, timeMode, result.InitialHealth, result.HitFrames, result.Seconds,
                    result.DamageTaken, snapshot.NegativeEffect, snapshot.TimeEffect, snapshot.SelectiveTime,
                    result.ObservedTimeModes
                }));
                return result;
            }
            if (snapshot.Enemies.Count == 0 || snapshot.IsDefeated)
                TestContext.WriteLine(JsonSerializer.Serialize(new { failed = true, weapon, powerup, spawnY, selective,
                    timeMode, tick, initial, lastHealth, hitFrames, snapshot.DamageTaken, snapshot.Progress,
                    snapshot.TimeEffect, observedTimeModes }));
            Assert.IsTrue(snapshot.Enemies.Count > 0 && !snapshot.IsDefeated,
                $"{weapon}/{powerup}/{timeMode}/selective={selective} did not kill the target before contact or expiry; last HP={lastHealth:0.000}, initial={initial:0.000}.");
        }
        Assert.Fail("The target outlived the killability probe's bounded approach window.");
        throw new InvalidOperationException();
    }

    [TestMethod]
    public void ModelHealthChoicesArePreservedWhenTheEnemyCannotReachContactDuringItsLifetime()
    {
        var profile = LivingArcadeKillability.CreateProfile(SavedHero(), 7);
        foreach (var raw in new[] { 2d, 20d, 200d })
        {
            var definition = Definition("non-contact", raw);
            var before = JsonSerializer.Serialize(definition);
            var plan = LivingArcadeKillability.CreateSpawnPlan(definition, -.03, profile);
            TestContext.WriteLine(JsonSerializer.Serialize(new { raw, plan.Health, plan.Evidence }));
            Assert.IsFalse(plan.Evidence.ReachesContact);
            Assert.AreEqual(raw, plan.Health);
            Assert.IsFalse(plan.Adjusted);
            Assert.AreEqual(before, JsonSerializer.Serialize(definition));
        }
        var slow = LivingArcadeKillability.CreateSpawnPlan(Definition("slow"), -.03, profile);
        var fast = LivingArcadeKillability.CreateSpawnPlan(Definition("fast"), -.03, profile);
        Assert.IsTrue(slow.Evidence.ReachesContact && fast.Evidence.ReachesContact);
        Assert.IsTrue(fast.Health < slow.Health, "A shorter approach must have a genuinely smaller available damage budget.");
    }

    [TestMethod]
    [DataRow("pistol", "slow")]
    [DataRow("shotgun", "slow")]
    [DataRow("smg", "slow")]
    [DataRow("pistol", "fast")]
    [DataRow("shotgun", "fast")]
    [DataRow("smg", "fast")]
    [DataRow("pistol", "hold-dash")]
    [DataRow("shotgun", "hold-dash")]
    [DataRow("smg", "hold-dash")]
    [DataRow("pistol", "full-shield")]
    [DataRow("shotgun", "full-shield")]
    [DataRow("smg", "full-shield")]
    [DataRow("pistol", "rotating-shield")]
    [DataRow("shotgun", "rotating-shield")]
    [DataRow("smg", "rotating-shield")]
    public void RealContinuousFireKillsBeforeContactWithWeakJamAndSelectiveSpeedUp(string weapon, string scenario)
    {
        var definition = Definition(scenario);
        var before = JsonSerializer.Serialize(definition);
        var result = Destroy(definition, weapon);
        Assert.IsTrue(result.Hero.TotalKills > 252, "Only a real kill grants experience; contact removes the target without it.");
        Assert.AreEqual(before, JsonSerializer.Serialize(definition));
    }

    public static IEnumerable<object[]> TimeModeCases()
    {
        foreach (var weapon in new[] { "pistol", "shotgun", "smg", "rockets" })
        foreach (var scenario in new[] { "long-full-shield", "long-rotating-shield" })
        foreach (var selective in new[] { false, true })
        foreach (var timeMode in new[] { "normal", "slow-down", "speed-up", "speed-then-slow", "slow-then-speed" })
            yield return [weapon, scenario, selective, timeMode];
    }

    [TestMethod]
    [DynamicData(nameof(TimeModeCases))]
    public void RealShieldCombatRemainsKillableAcrossTimeModesAndMidFightSwitches(
        string loadout, string scenario, bool selective, string timeMode)
    {
        var definition = Definition(scenario);
        var before = JsonSerializer.Serialize(definition);
        var weapon = loadout == "rockets" ? "pistol" : loadout;
        var powerup = loadout == "rockets" ? "weapon_rocket" : "";
        var baselinePlan = LivingArcadeKillability.CreateSpawnPlan(definition, -.03,
            LivingArcadeKillability.CreateProfile(SavedHero(weapon), 7));
        var fight = Destroy(definition, weapon, powerup, selective: selective, timeMode: timeMode);
        Assert.AreEqual(baselinePlan.Health, fight.InitialHealth, 1e-10,
            "A time choice or temporary rocket must not silently increase the planned enemy HP.");
        Assert.IsTrue(fight.Hero.TotalKills > 252, "A real kill, rather than body contact or expiry, must award experience.");
        if (timeMode.Contains("then", StringComparison.Ordinal))
        {
            CollectionAssert.Contains(fight.ObservedTimeModes.ToArray(), "speed_up");
            CollectionAssert.Contains(fight.ObservedTimeModes.ToArray(), "slow_down");
        }
        Assert.AreEqual(before, JsonSerializer.Serialize(definition));
    }

    [TestMethod]
    [DataRow("full-shield")]
    [DataRow("rotating-shield")]
    public void ImpossibleShieldWindowGetsAnExplicitReachableSpawnPlan(string scenario)
    {
        var definition = Definition(scenario);
        var profile = LivingArcadeKillability.CreateProfile(SavedHero(), 7);
        var impossible = LivingArcadeKillability.Evaluate(definition, -.03, profile);
        Assert.IsTrue(impossible.ReachesContact && impossible.NoWindow);
        var plan = LivingArcadeKillability.CreateSpawnPlan(definition, -.03, profile);
        Assert.IsTrue(plan.Adjusted);
        Assert.IsFalse(string.IsNullOrWhiteSpace(plan.AdjustmentReason));
        Assert.IsFalse(plan.Evidence.NoWindow);
        Assert.IsTrue(plan.Health > 0 && plan.Evidence.Budget >= plan.Health);
        Assert.IsTrue(plan.ForceVulnerable || plan.EntranceHoldSeconds > 0);
    }

    [TestMethod]
    public void TemporaryUpgradesDoNotIncreaseHealthAndStillMakeActualKillsFaster()
    {
        var baseline = Destroy(Definition("slow"), "pistol");
        foreach (var powerup in new[] { "power", "rapid", "weapon_laser" })
        {
            var boosted = Destroy(Definition("slow"), "pistol", powerup);
            Assert.AreEqual(baseline.InitialHealth, boosted.InitialHealth, 1e-10,
                "Temporary strength must not be cancelled by growing enemy HP.");
            Assert.IsTrue(boosted.Seconds < baseline.Seconds, $"{powerup} must help through real collisions.");
        }
    }

    [TestMethod]
    [DataRow("pistol", .7)]
    [DataRow("shotgun", .7)]
    [DataRow("smg", .7)]
    [DataRow("pistol", .8)]
    [DataRow("shotgun", .8)]
    [DataRow("smg", .8)]
    public void LateBirthUsesARealGuardInsteadOfAnUnkillablePositiveHpFloor(string weapon, double spawnY)
    {
        var definition = Definition("fast");
        var plan = LivingArcadeKillability.CreateSpawnPlan(definition, spawnY,
            LivingArcadeKillability.CreateProfile(SavedHero(weapon), 7));
        Assert.IsTrue(plan.Adjusted);
        Assert.IsTrue(plan.SpawnY < spawnY || plan.EntranceHoldSeconds > 0);
        var result = Destroy(definition, weapon, spawnY: spawnY);
        Assert.IsTrue(result.Hero.TotalKills > 252);
    }

    [TestMethod]
    public void ARealLateParentKillPassesItsChildThroughTheSameBirthGuard()
    {
        var child = Definition("fast"); child.Id = "child";
        var parent = Definition("slow", .25); parent.Id = "parent";
        parent.DeathSpawnId = child.Id; parent.DeathSpawnCount = 1;
        var level = Level(parent, child);
        var original = JsonSerializer.Serialize(level);
        var simulation = new LivingArcadeSimulation(level, SavedHero("shotgun"), attempts: 2,
            deathProgress: 10000, penalty: "weak");
        Invoke(simulation, "SpawnEnemy", parent.Id, .5, -.03);
        // This is a reachable normal-play position; isolate the parent's late death without replaying the whole approach.
        SetEnemyY(simulation, .76);
        for (var tick = 0; tick < 120 && simulation.ResultHero.TotalKills == 252; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(253L, simulation.ResultHero.TotalKills);
        var born = simulation.Snapshot.Enemies.Single();
        Assert.IsTrue(born.Id.StartsWith("child-", StringComparison.Ordinal));
        Assert.IsTrue(born.Y < .74 || born.Telegraph, "A death-spawn must not skip the close-birth guard.");
        Set(simulation, "_negative", "jam"); Set(simulation, "_negativeSeconds", 6d);
        Set(simulation, "_fireClock", 1 / simulation.Snapshot.FireRate);
        for (var tick = 0; tick < 30 * 60 && simulation.ResultHero.TotalKills == 253; tick++)
            simulation.Advance(1d / 60, new(SpeedUp: tick == 0));
        Assert.AreEqual(254L, simulation.ResultHero.TotalKills, "The actual child must be destroyed before contact.");
        Assert.AreEqual(0d, simulation.Snapshot.DamageTaken);
        Assert.AreEqual(original, JsonSerializer.Serialize(level));
    }

    [TestMethod]
    public void SweptCollisionCountsATinyFastTargetCrossedBetweenFrameEndpoints()
    {
        var definition = Definition("tiny-fast");
        var simulation = new LivingArcadeSimulation(Level(definition), SavedHero());
        Invoke(simulation, "SpawnEnemy", definition.Id, .5, -.03);
        SetEnemyY(simulation, .5);
        Set(simulation, "_fireClock", 10d);
        var before = simulation.Snapshot.Enemies.Single().Health;
        // Both relative endpoints are outside radius .012 + .009; the segment crosses the target.
        Invoke(simulation, "AddShot", .5, .525, 0d, 1.5d, false, 1d, 0d, null);
        simulation.Advance(1d / 60, new(SpeedUp: true));
        var snapshot = simulation.Snapshot;
        Assert.IsTrue((1.5 + .6 * 1.5) * snapshot.TempoMultiplier / 60 > 2 * (.012 + .009));
        Assert.IsTrue(simulation.ResultHero.TotalKills > 252
            || snapshot.Enemies.Single().Health < before, "A swept hit must not be lost between endpoint samples.");
    }

    [TestMethod]
    public void HostStageBossIsExcludedFromOrdinaryKillabilityLimits()
    {
        var target = Definition("fast", .25);
        var level = Level(target) with { Number = 3, DurationSeconds = 30,
            Waves = [new() { At = 0, Count = 1, EnemyId = target.Id, X = .1 }] };
        var simulation = new LivingArcadeSimulation(level, SavedHero());
        for (var tick = 0; tick < 40 * 60 && simulation.Snapshot.BossMaxHealth == 0; tick++)
            simulation.Advance(1d / 60, default);
        var boss = simulation.Snapshot;
        TestContext.WriteLine(JsonSerializer.Serialize(new { boss.BossHealth, boss.BossMaxHealth, boss.BossPhase }));
        Assert.IsTrue(boss.Enemies.Any(enemy => enemy.IsBoss));
        Assert.IsTrue(boss.BossMaxHealth >= 70);
        Assert.AreEqual(boss.BossMaxHealth, boss.BossHealth);
        Assert.AreEqual(1, boss.BossPhase);
        Assert.IsFalse(boss.IsComplete);
    }
}
