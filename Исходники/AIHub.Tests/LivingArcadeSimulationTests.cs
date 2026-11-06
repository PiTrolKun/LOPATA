using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeSimulationTests
{
    private static LivingArcadeLevel QuietLevel() => LivingArcadeLevelPolicy.CreateFirst(123) with
    {
        DurationSeconds = 300,
        Waves = [new() { At = 250, Count = 1, X = .5 }],
        MiniEvents = []
    };
    private static void Advance(LivingArcadeSimulation simulation, double seconds, LivingArcadeInput input = default)
    { for (var i = 0; i < (int)Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, input); }

    [TestMethod]
    public void FireTiltsOnlyWhileActuallyMovingAndReturnsForwardAtTheRoadEdge()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(), new());
        simulation.Advance(1d / 60, new(Right: true));
        Assert.AreEqual(Math.PI / 18, simulation.Snapshot.AngleRadians, 1e-8);
        simulation.Advance(1d / 60, default);
        Assert.AreEqual(0, simulation.Snapshot.AngleRadians, 1e-8);
        Advance(simulation, 3, new(Right: true));
        Assert.AreEqual(.935, simulation.Snapshot.PlayerX, 1e-8);
        Assert.AreEqual(0, simulation.Snapshot.AngleRadians, 1e-8);
    }

    [TestMethod]
    public void CooldownsAreIndependentRealActiveTimeAndHeldKeysDoNotRetrigger()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(), new());
        Advance(simulation, 2, new(SpeedUp: true));
        Assert.AreEqual(118 + 1d / 60, simulation.Snapshot.SpeedUpCooldown, .02);
        Assert.AreEqual(0, simulation.Snapshot.SlowDownCooldown);
        Assert.AreEqual(3, simulation.Snapshot.Progress, .02);
        simulation.SetPaused(true); Advance(simulation, 10);
        Assert.AreEqual(118 + 1d / 60, simulation.Snapshot.SpeedUpCooldown, .02);
        simulation.SetPaused(false);
        simulation.Advance(1d / 60, new(Super: true));
        Assert.AreEqual(120, simulation.Snapshot.SuperCooldown, .02);
        Assert.IsTrue(simulation.Snapshot.SpeedUpCooldown < 119);
        Assert.AreEqual(0, simulation.Snapshot.SlowDownCooldown);
    }

    [TestMethod]
    public void PenaltyExpiresAtWorldProgressRatherThanElapsedRealTime()
    {
        var simulation = new LivingArcadeSimulation(QuietLevel(), new(), 1, 3, "heavy");
        Advance(simulation, 1.5, new(SpeedUp: true));
        Assert.AreEqual("heavy", simulation.Snapshot.Penalty);
        Advance(simulation, .6);
        Assert.AreEqual("", simulation.Snapshot.Penalty);
    }

    [TestMethod]
    public void FixedStepMakesMovementIndependentOfRenderFrequency()
    {
        var first = new LivingArcadeSimulation(QuietLevel(), new());
        var second = new LivingArcadeSimulation(QuietLevel(), new());
        Advance(first, .5, new(Left: true));
        for (var i = 0; i < 15; i++) second.Advance(1d / 30, new(Left: true));
        Assert.AreEqual(first.Snapshot.PlayerX, second.Snapshot.PlayerX, 1e-8);
        Assert.AreEqual(first.Snapshot.Progress, second.Snapshot.Progress, 1e-8);
        Assert.AreEqual(first.Snapshot.Shots.Count, second.Snapshot.Shots.Count);
    }

    [TestMethod]
    public void AuthoredLevelAndGeneratedFiniteStateMechanicsRoundTripThroughStrictContract()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(12);
        var parsed = LivingArcadeLevelPolicy.Parse(JsonSerializer.Serialize(level, LivingArcadeLevelPolicy.JsonOptions));
        Assert.IsTrue(parsed.Authored);
        Assert.IsTrue(parsed.EnemyTypes.Any(x => x.States.Any(s => s.Shield)));
        Assert.IsTrue(parsed.EnemyTypes.Any(x => x.States.Any(s => s.ShotCount > 1)));
        var unknown = JsonSerializer.Serialize(level, LivingArcadeLevelPolicy.JsonOptions).TrimEnd()[..^1] + ",\"shell\":\"cmd.exe\"}";
        Assert.ThrowsExactly<JsonException>(() => LivingArcadeLevelPolicy.Parse(unknown));
        var duplicate = JsonSerializer.Serialize(level, LivingArcadeLevelPolicy.JsonOptions).TrimEnd()[..^1] + ",\"number\":1}";
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Parse(duplicate));
    }

    [TestMethod]
    public void UnsafeNumbersStateReferencesAndRecursiveDeathSpawnsAreRejected()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(3);
        level.EnemyTypes[0].States[0].NextState = "missing";
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level));
        level = LivingArcadeLevelPolicy.CreateFirst(3);
        level.EnemyTypes[0].DeathSpawnCount = 1; level.EnemyTypes[0].DeathSpawnId = level.EnemyTypes[0].Id;
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level));
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level with { DurationSeconds = double.NaN }));
    }

    [TestMethod]
    public void DifficultyResetKeepsPermanentProgressAndPartialCarryInterpolatesTemporaryUpgrades()
    {
        var hero = new LivingArcadeHeroState { PowerupBalanceVersion = 1,
            PermanentPower = 1.2, WeaponPower = 1.3, FireRate = 6, MaxHealth = 120, SpreadShots = 3 };
        var level = QuietLevel() with { ResetDifficulty = true };
        var reset = LivingArcadeLevelPolicy.Transfer(hero, level);
        Assert.AreEqual(1.2, reset.PermanentPower); Assert.AreEqual(1, reset.WeaponPower);
        Assert.AreEqual(5, reset.FireRate); Assert.AreEqual(100, reset.MaxHealth); Assert.AreEqual(1, reset.SpreadShots);
        var partial = LivingArcadeLevelPolicy.Transfer(hero, level with { ResetDifficulty = false, CarryFraction = .5 });
        Assert.AreEqual(1.2, partial.PermanentPower); Assert.AreEqual(1.15, partial.WeaponPower, 1e-10);
        Assert.AreEqual(5.5, partial.FireRate); Assert.AreEqual(110, partial.MaxHealth); Assert.AreEqual(1, partial.SpreadShots);
    }

    [TestMethod]
    public void BurstContentRemainsBoundedUnderAFullLevelSimulation()
    {
        var level = QuietLevel() with { DurationSeconds = 30, Waves = Enumerable.Range(0, 33)
            .Select(i => new LivingArcadeWave { At = 0, Count = 12, SpawnInterval = 0, X = .5 }).ToList() };
        var simulation = new LivingArcadeSimulation(level, new() { MaxHealth = 1000 });
        Advance(simulation, 2);
        Assert.IsTrue(simulation.Snapshot.Enemies.Count <= 80);
        Assert.IsTrue(simulation.Snapshot.Shots.Count <= 512);
        Assert.IsTrue(double.IsFinite(simulation.Snapshot.Health));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => simulation.Advance(double.PositiveInfinity, default));
    }

    [TestMethod]
    public void StateTransitionChangesShieldAndEmitsAProjectileFan()
    {
        var level = QuietLevel() with
        {
            EnemyTypes = [new() { Id = "switcher", Health = 100, Speed = .2,
                States = [new() { Shield = true, Duration = .2, NextState = "open" },
                    new() { Id = "open", Duration = 10, NextState = "open", ShotInterval = .4, ShotCount = 3, ShotSpread = 40 }] }],
            Waves = [new() { At = 0, EnemyId = "switcher", Count = 1, X = .8, SpawnInterval = 0 }]
        };
        var simulation = new LivingArcadeSimulation(level, new());
        Advance(simulation, .1); Assert.AreEqual("#73DACA", simulation.Snapshot.Enemies[0].Color);
        Advance(simulation, .9); Assert.AreNotEqual("#73DACA", simulation.Snapshot.Enemies[0].Color);
        Assert.IsTrue(simulation.Snapshot.Shots.Count(s => s.Hostile) >= 3);
    }

    [TestMethod]
    public void DestroyingAParentCreatesBoundedChildEntitiesFromItsDefinition()
    {
        var level = QuietLevel() with
        {
            EnemyTypes = [new() { Id = "parent", Health = .25, Speed = .2, DeathSpawnId = "child", DeathSpawnCount = 2 },
                new() { Id = "child", Health = 100, Speed = .2 }],
            Waves = [new() { At = 0, EnemyId = "parent", Count = 1, X = .5, SpawnInterval = 0 }]
        };
        var simulation = new LivingArcadeSimulation(level, new());
        // Observe the production death-spawn immediately. A later fixed sample can already contain child kills.
        for (var tick = 0; tick < 120 && simulation.ResultHero.TotalKills == 0; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1L, simulation.ResultHero.TotalKills, "The parent must be destroyed by an actual projectile.");
        Assert.AreEqual(2, simulation.Snapshot.Enemies.Count(x => x.Id.StartsWith("child-", StringComparison.Ordinal)));
        Assert.IsFalse(simulation.Snapshot.Enemies.Any(x => x.Id.StartsWith("parent-", StringComparison.Ordinal)));
    }
}

[TestClass]
public sealed class LivingArcadeProbeTests
{
    [TestMethod]
    public void ProbeRejectsCapOverflowButNeverRejectsOnlyBecauseABotLoses()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(14);
        var result = LivingArcadeLevelPolicy.Probe(level);
        Assert.IsTrue(result.Accepted); Assert.IsNull(result.RejectionKey);
        Assert.IsTrue(result.SimulationTicks <= 90_000); Assert.IsTrue(result.Trajectories.Count > 0);
        var many = level with { Waves = Enumerable.Range(0, 10).Select(_ => new LivingArcadeWave
            { At = 0, Count = 12, SpawnInterval = 0, EnemyId = "scout" }).ToList() };
        var overload = LivingArcadeLevelPolicy.Probe(many);
        Assert.IsFalse(overload.Accepted); Assert.AreEqual("LivingArcade.LevelCostExceeded", overload.RejectionKey);
        Assert.AreEqual(0, overload.SimulationTicks);
        var fragile = LivingArcadeLevelPolicy.Probe(level, new() { MaxHealth = 1 });
        Assert.IsTrue(fragile.Accepted);
    }

    [TestMethod]
    public void UnreachableOrOnlyTransientOpenStatesDoNotHideAnInvulnerableCycle()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(14);
        var enemy = level.EnemyTypes[0];
        enemy.States = [new() { NextState = "locked", Duration = .2 },
            new() { Id = "locked", NextState = "locked", Shield = true },
            new() { Id = "unused", NextState = "unused", Shield = false }];
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeLevelPolicy.Validate(level));
    }
}

[TestClass]
public sealed class LivingArcadeRunStoreTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-arcade-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-arcade-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    [TestMethod]
    public void ThreeSlotsRenameDeleteAndLateGenerationCannotResurrectDeletedRun()
    {
        var store = new LivingArcadeRunStore(_root);
        var first = store.Create("Первое", 1); store.Create("Second", 2); store.Create("Third", 3);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Create("Fourth"));
        store.Rename(first.Id, "Моё прохождение");
        Assert.AreEqual("Моё прохождение", new LivingArcadeRunStore(_root).Load(first.Id)!.Name);
        Assert.IsTrue(store.Delete(first.Id));
        Assert.IsFalse(store.TrySaveLevel(first.Id, LivingArcadeLevelPolicy.CreateFirst(2) with { Number = 2 }));
        Assert.IsFalse(store.TrySaveDraft(first.Id, new() { Number = 3, Concept = "late" }));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(first));
        Assert.AreEqual(2, store.List().Count); store.Create("Replacement"); Assert.AreEqual(3, store.List().Count);
    }

    [TestMethod]
    public void ConcurrentGenerationAndProgressMutationPreserveBothAndAdvanceExactlyOnce()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 7);
        Parallel.Invoke(() => store.Update(run.Id, current => { current.Attempts = 2; current.DeathProgress = 20; }),
            () => Assert.IsTrue(store.TrySaveLevel(run.Id, LivingArcadeLevelPolicy.CreateFirst(8) with { Number = 2, ResetDifficulty = true })));
        var saved = store.Load(run.Id)!; Assert.AreEqual(2, saved.Attempts); Assert.IsTrue(saved.Levels.ContainsKey(2));
        var finished = new LivingArcadeSnapshot { IsComplete = true, Progress = 120, BonusCount = 3 };
        var hero = new LivingArcadeHeroState { PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
            PermanentPower = 1.2, WeaponPower = 1.3 };
        store.CompleteLevel(run.Id, hero, finished); store.CompleteLevel(run.Id, hero, finished);
        Assert.AreEqual(1, store.Load(run.Id)!.History.Count);
        Assert.IsTrue(store.TryAdvanceToReadyLevel(run.Id, out var next));
        Assert.AreEqual(2, next!.CurrentLevel); Assert.AreEqual(1.2, next.EntryState.PermanentPower);
        Assert.AreEqual(1, next.EntryState.WeaponPower); Assert.AreEqual(0, next.Attempts);
        Assert.IsFalse(store.TryAdvanceToReadyLevel(run.Id, out _));
    }

    [TestMethod]
    public void CompletedWaitingRunAndFirstEventsSurviveRestartWithoutChangingAuthoredCore()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 4);
        Assert.IsTrue(store.TrySaveFirstMiniEvents(run.Id, [new() { At = 20, Kind = "fog" }]));
        Assert.IsFalse(store.TrySaveFirstMiniEvents(run.Id, [new() { At = 25 }]));
        store.CompleteLevel(run.Id, new(), new() { IsComplete = true, Progress = 120 });
        Assert.IsFalse(store.TryAdvanceToReadyLevel(run.Id, out _));
        var reopened = new LivingArcadeRunStore(_root).Load(run.Id)!;
        Assert.IsNotNull(reopened.PendingHero); Assert.IsTrue(reopened.FirstEventsPrepared);
        Assert.AreEqual(25, reopened.Levels[1].Waves.Count); Assert.AreEqual(5, reopened.Levels[1].MiniEvents.Count);
        Assert.IsTrue(File.Exists(Path.Combine(_root, run.Id, "run.json.bak")));
    }

    [TestMethod]
    public void DamagedManifestCanRecoverPreviousCommitButANewerSchemaIsNeverDowngraded()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 11);
        store.Update(run.Id, current => current.Attempts = 2);
        var path = Path.Combine(_root, run.Id, "run.json"); File.WriteAllText(path, "{broken");
        var recovered = store.Load(run.Id)!;
        Assert.IsTrue(recovered.RecoveredFromBackup); Assert.AreEqual(0, recovered.Attempts);
        store.Update(run.Id, current => current.Attempts = 5);
        Assert.AreEqual(5, store.Load(run.Id)!.Attempts);
        Assert.IsFalse(store.Load(run.Id)!.RecoveredFromBackup);
        Assert.IsTrue(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "run.json.damaged-*").Any());
        File.WriteAllText(path, "{\"schemaVersion\":2}");
        Assert.ThrowsExactly<NotSupportedException>(() => store.Load(run.Id));
    }

    [TestMethod]
    public void ReachingPreviousDeathMarkerClearsPenaltyDurablyAndAnotherDeathGetsANewMarker()
    {
        var store = new LivingArcadeRunStore(_root); var run = store.Create("Run", 11);
        var first = store.RecordDefeat(run.Id, 40);
        Assert.IsFalse(store.ClearPenaltyIfReached(run.Id, 39));
        var earlier = store.RecordDefeat(run.Id, 20);
        Assert.AreEqual(first.Penalty, earlier.Penalty); Assert.AreEqual(40, earlier.DeathProgress);
        Assert.IsTrue(store.ClearPenaltyIfReached(run.Id, 40));
        Assert.AreEqual("", new LivingArcadeRunStore(_root).Load(run.Id)!.Penalty);
        var next = store.RecordDefeat(run.Id, 10); Assert.AreEqual(10, next.DeathProgress);
        Assert.IsFalse(string.IsNullOrWhiteSpace(next.Penalty));
    }

    [TestMethod]
    public void StaleManifestCannotRollBackConcurrentProgressAndPendingHeroMustBeFinite()
    {
        var store = new LivingArcadeRunStore(_root); var stale = store.Create("Run", 11);
        store.RecordDefeat(stale.Id, 25);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(stale));
        Assert.AreEqual(1, store.Load(stale.Id)!.Attempts);
        Assert.ThrowsExactly<InvalidDataException>(() => store.CompleteLevel(stale.Id,
            new() { WeaponPower = double.NaN }, new() { IsComplete = true, Progress = 120 }));
        Assert.IsNull(store.Load(stale.Id)!.PendingHero);
    }
}
