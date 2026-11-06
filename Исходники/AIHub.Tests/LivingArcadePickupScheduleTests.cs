using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePickupScheduleTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, LivingArcadeLevelPolicy.JsonOptions);

    [TestMethod]
    public void TutorialGetsNinePositivePickupsWithoutChangingItsSourceAndRepeatedPreparationIsStable()
    {
        var source = LivingArcadeLevelPolicy.CreateFirst(43);
        var original = Json(source);
        var played = LivingArcadePickupSchedule.ForPlay(source);
        Assert.AreEqual(original, Json(source));
        Assert.AreEqual(9, played.MiniEvents.Count(e => e.Kind == "bonus"));
        Assert.AreEqual(0, played.MiniEvents.Count(e => e.Kind == "negative"));
        foreach (var tutorial in source.MiniEvents.Where(e => e.Tutorial))
            Assert.IsTrue(played.MiniEvents.Any(e => Json(e) == Json(tutorial)));
        Assert.AreEqual(Json(played.MiniEvents), Json(LivingArcadePickupSchedule.ForPlay(source).MiniEvents));
        Assert.AreEqual(Json(played.MiniEvents), Json(LivingArcadePickupSchedule.ForPlay(played).MiniEvents));
    }

    [TestMethod]
    [DataRow(2, 2)]
    [DataRow(6, 3)]
    [DataRow(10, 4)]
    [DataRow(30, 4)]
    public void LaterLevelsReceiveSparseBoundedNegativePickupsAndTheFullPositivePool(int number, int negativeCount)
    {
        var source = LivingArcadeLevelPolicy.CreateFirst(44) with { Number = number, Authored = false, MiniEvents = [] };
        var original = Json(source);
        var played = LivingArcadePickupSchedule.ForPlay(source);
        Assert.AreEqual(original, Json(source));
        Assert.AreEqual(9, played.MiniEvents.Count(e => e.Kind == "bonus"));
        Assert.AreEqual(negativeCount, played.MiniEvents.Count(e => e.Kind == "negative"));
        Assert.IsTrue(played.MiniEvents.All(e => e.At >= 0 && e.At <= played.DurationSeconds && e.X is >= .08 and <= .92));
        Assert.AreEqual(Json(played.MiniEvents), Json(LivingArcadePickupSchedule.ForPlay(played).MiniEvents));
    }

    [TestMethod]
    public void FullInputEventBudgetIsPreservedAndShortLevelsNeverScheduleOutsideTheirBounds()
    {
        var full = LivingArcadeLevelPolicy.CreateFirst(45) with
        {
            Number = 10,
            MiniEvents = Enumerable.Range(0, 32).Select(i => new LivingArcadeMiniEvent
                { At = i * 3, Kind = "fog", X = .5 }).ToList()
        };
        var prepared = LivingArcadePickupSchedule.ForPlay(full);
        Assert.AreEqual(32, prepared.MiniEvents.Count);
        Assert.AreEqual(Json(full.MiniEvents), Json(prepared.MiniEvents));
        var shortLevel = LivingArcadeLevelPolicy.CreateFirst(46) with
        {
            Number = 2, DurationSeconds = 30, MiniEvents = [],
            Waves = [new() { At = 5, Count = 1 }]
        };
        var shortPrepared = LivingArcadePickupSchedule.ForPlay(shortLevel);
        Assert.IsTrue(shortPrepared.MiniEvents.Count <= 32);
        Assert.IsTrue(shortPrepared.MiniEvents.All(e => e.At >= 0 && e.At <= 30));
        LivingArcadeLevelPolicy.Validate(shortPrepared);
    }

    [TestMethod]
    [DataRow("bonus", "jam")]
    [DataRow("negative", "power")]
    [DataRow("bonus", "unknown")]
    public void ExplicitPickupKindsCannotCrossThePositiveNegativeContract(string eventKind, string bonusKind)
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(47) with
        {
            MiniEvents = [new() { At = 5, Kind = eventKind, BonusKind = bonusKind }]
        };
        Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadePickupSchedule.ForPlay(level));
    }

    [TestMethod, DoNotParallelize]
    public void ReplayExplicitSavedRunWithoutWritingItsFilesAndRecordCurrentBalanceEvidence()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_ARCADE_BALANCE_RUN");
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_BALANCE_OUTPUT");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output))
        {
            Assert.Inconclusive("Explicit saved-run path and separate evidence directory required.");
            return;
        }
        var sourcePath = Path.GetFullPath(source);
        var outputPath = Path.GetFullPath(output);
        var runDirectory = Path.GetDirectoryName(sourcePath)! + Path.DirectorySeparatorChar;
        Assert.IsFalse((outputPath + Path.DirectorySeparatorChar).StartsWith(runDirectory, StringComparison.OrdinalIgnoreCase),
            "Evidence must be written outside the user's saved run.");
        var bytes = File.ReadAllBytes(sourcePath);
        var originalHash = SHA256.HashData(bytes);
        var run = JsonSerializer.Deserialize<LivingArcadeRun>(bytes, LivingArcadeLevelPolicy.JsonOptions)!;
        var hero = LivingArcadeHeroBalance.Normalize(run.EntryState);
        Assert.AreEqual(1, hero.SpreadShots);
        Assert.IsTrue(hero.WeaponPower <= LivingArcadeHeroBalance.MaximumWeaponPower);
        Assert.IsTrue(hero.FireRate <= LivingArcadeHeroBalance.MaximumFireRate);
        Assert.IsTrue(hero.PermanentPower <= LivingArcadeHeroBalance.MaximumPermanentPower);
        Assert.IsTrue(hero.MaxHealth <= LivingArcadeHeroBalance.MaximumHealth);
        var evidence = new List<object>();
        foreach (var saved in run.Levels.OrderBy(pair => pair.Key))
        {
            var played = LivingArcadePickupSchedule.ForPlay(LivingArcadePacing.ForPlay(saved.Value));
            var simulation = new LivingArcadeSimulation(played, run.EntryState);
            for (var i = 0; i < 1800 && !simulation.Snapshot.IsComplete && !simulation.Snapshot.IsDefeated; i++)
                simulation.Advance(.25, new(Left: i % 48 < 12, Right: i % 48 is >= 24 and < 36));
            Assert.IsTrue(simulation.Snapshot.IsComplete || simulation.Snapshot.IsDefeated);
            evidence.Add(new
            {
                level = saved.Key, tempo = simulation.Snapshot.TempoMultiplier,
                positivePickups = played.MiniEvents.Count(e => e.Kind == "bonus"),
                negativePickups = played.MiniEvents.Count(e => e.Kind == "negative"),
                simulation.Snapshot.Progress, simulation.Snapshot.Health,
                simulation.Snapshot.IsComplete, simulation.Snapshot.IsDefeated,
                simulation.Snapshot.CharacterLevel, simulation.ResultHero.TotalKills
            });
        }
        var bossLevel = LivingArcadeLevelPolicy.CreateFirst(48) with
        {
            Number = 3, DurationSeconds = 30, MiniEvents = [],
            Waves = [new() { At = 0, Count = 1, X = .1 }]
        };
        var bossSimulation = new LivingArcadeSimulation(bossLevel, new());
        for (var i = 0; i < 2400 && bossSimulation.Snapshot.BossPhase == 0; i++)
            bossSimulation.Advance(1d / 60, default);
        Assert.AreEqual(3, bossSimulation.Snapshot.GameLevel);
        Assert.IsTrue(bossSimulation.Snapshot.BossPhase > 0);
        Directory.CreateDirectory(outputPath);
        File.WriteAllText(Path.Combine(outputPath, "saved-run-balance.json"), Json(new
        {
            originalHero = run.EntryState, normalizedHero = hero, levels = evidence,
            thirdLevelBoss = new { bossSimulation.Snapshot.GameLevel, bossSimulation.Snapshot.BossPhase,
                bossSimulation.Snapshot.BossHealth, bossSimulation.Snapshot.BossMaxHealth },
            originalSha256 = Convert.ToHexString(originalHash)
        }));
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(File.ReadAllBytes(sourcePath)));
    }
}
