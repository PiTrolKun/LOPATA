using AIHub.Models;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.LivingArcade;
using System.Reflection;
using System.Windows.Controls;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeBonusFeedbackTests
{
    private static LivingArcadeLevel Level(string? bonus = "spread") => LivingArcadeLevelPolicy.CreateFirst(71) with
    {
        DurationSeconds = 300,
        Waves = [new() { At = 250, Count = 1 }],
        MiniEvents = bonus is null ? [] : [new() { At = 0, X = .5, Strength = 1, BonusKind = bonus }]
    };
    private static void Advance(LivingArcadeSimulation simulation, double seconds)
    {
        for (var i = 0; i < (int)Math.Round(seconds * 60); i++) simulation.Advance(1d / 60, default);
    }

    [TestMethod]
    public void SpreadPickupChangesTheActualFanWithoutAlsoRaisingDamageAndFireRate()
    {
        var upgraded = new LivingArcadeSimulation(Level(), new());
        var control = new LivingArcadeSimulation(Level(null), new());
        Advance(upgraded, 8); Advance(control, 8);
        Assert.AreEqual(1, upgraded.Snapshot.BonusCount);
        Assert.AreEqual(1, upgraded.Snapshot.ProjectileDamage, 1e-8);
        Assert.AreEqual(5, upgraded.Snapshot.FireRate, 1e-8);
        Assert.AreEqual(3, upgraded.Snapshot.SpreadShots);
        Assert.IsTrue(upgraded.Snapshot.Shots.Count(s => !s.Hostile) > control.Snapshot.Shots.Count(s => !s.Hostile));
        Assert.IsTrue(upgraded.Snapshot.Shots.Any(s => !s.Hostile && s.X < .49));
        Assert.IsTrue(upgraded.Snapshot.Shots.Any(s => !s.Hostile && s.X > .51));
        var receipt = upgraded.Snapshot.RecentBonus!;
        Assert.AreEqual(0, receipt.DamageGain, 1e-8);
        Assert.AreEqual(0, receipt.FireRateGain, 1e-8);
        Assert.AreEqual(2, receipt.ExtraShots);
        Assert.AreEqual(0, receipt.HealthRestored);
        Assert.AreEqual("spread", receipt.Kind);
        Assert.IsTrue(receipt.DurationSeconds is > 0 and <= 10);
    }

    [TestMethod]
    public void WeaponBonusChangesDamageInRealEnemyCollisions()
    {
        LivingArcadeLevel Target(bool bonus) => Level(bonus ? "power" : null) with
        {
            EnemyTypes = [new() { Id = "target", Health = 1.4, Speed = .015, Radius = .025 }],
            Waves = [new() { At = 10, Count = 1, EnemyId = "target", X = .5 }]
        };
        var upgraded = new LivingArcadeSimulation(Target(true), new() { FireRate = 1 });
        var control = new LivingArcadeSimulation(Target(false), new() { FireRate = 1 });
        Advance(upgraded, 10); Advance(control, 10);
        Assert.AreEqual(1, upgraded.Snapshot.Enemies.Count); Assert.AreEqual(1, control.Snapshot.Enemies.Count);
        double upgradedKilledAt = 0, controlKilledAt = 0;
        for (var i = 0; i < 300; i++)
        {
            upgraded.Advance(1d / 60, default); control.Advance(1d / 60, default);
            if (upgradedKilledAt == 0 && upgraded.Snapshot.Enemies.Count == 0) upgradedKilledAt = upgraded.Snapshot.Progress;
            if (controlKilledAt == 0 && control.Snapshot.Enemies.Count == 0) controlKilledAt = control.Snapshot.Progress;
        }
        Assert.IsTrue(upgradedKilledAt > 0 && controlKilledAt > 0);
        Assert.IsTrue(upgradedKilledAt < controlKilledAt, $"Upgraded {upgradedKilledAt}; control {controlKilledAt}");
    }

    [TestMethod]
    public void HealingReceiptReportsActualRecoveryAndFeedbackPausesWithTheGame()
    {
        var level = Level("repair") with
        {
            EnemyTypes = [new() { Id = "contact", Health = 200, Speed = .2 }],
            Waves = [new() { At = 0, Count = 1, EnemyId = "contact", X = .5 }]
        };
        var simulation = new LivingArcadeSimulation(level, new());
        // This fixture isolates contact and healing. Continuous fire would now correctly prevent the contact.
        typeof(LivingArcadeSimulation).GetField("_fireClock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(simulation, 100d);
        Advance(simulation, 8);
        Assert.AreEqual(14, simulation.Snapshot.DamageTaken, 1e-8);
        Assert.AreEqual(98, simulation.Snapshot.Health, 1e-8);
        Assert.AreEqual(12, simulation.Snapshot.RecentBonus!.HealthRestored, 1e-8);
        var remaining = simulation.Snapshot.BonusFeedbackSeconds;
        simulation.SetPaused(true); Advance(simulation, 10);
        Assert.AreEqual(remaining, simulation.Snapshot.BonusFeedbackSeconds);
        simulation.SetPaused(false); Advance(simulation, 4);
        Assert.IsNull(simulation.Snapshot.RecentBonus);
        Assert.AreEqual(0, simulation.Snapshot.BonusFeedbackSeconds);
    }

    [TestMethod]
    public void RepeatedPermanentBonusesStaySmallAndDoNotCreateAPermanentFan()
    {
        var level = Level() with { MiniEvents = Enumerable.Range(0, 20)
            .Select(_ => new LivingArcadeMiniEvent { At = 0, X = .5, Strength = 3, BonusKind = "weapon" }).ToList() };
        var simulation = new LivingArcadeSimulation(level, new());
        Advance(simulation, 8);
        Assert.AreEqual(20, simulation.Snapshot.BonusCount);
        Assert.IsTrue(simulation.ResultHero.WeaponPower is > 1 and <= 1.35);
        Assert.AreEqual(5, simulation.ResultHero.FireRate);
        Assert.AreEqual(1, simulation.ResultHero.SpreadShots);
        Assert.AreEqual(0, simulation.Snapshot.TimeScopeBonusCount);
        Assert.AreEqual("", simulation.Snapshot.PowerupEffect);
    }

    [TestMethod, DoNotParallelize]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ActualHudShowsWeaponStatsAndCollectedBonusInBothLanguages(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-bonus-hud-" + Guid.NewGuid().ToString("N"));
        try
        {
            var simulation = new LivingArcadeSimulation(Level(), new());
            Advance(simulation, 8);
            var localizer = new LocalizationService(); localizer.Load(language);
            var store = new LivingArcadeRunStore(directory);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            LivingArcadeGameHudTests.StartFixtureRun(page, store);
            LivingArcadeGameHudTests.Frame(page, simulation.Snapshot);
            var hud = LivingArcadeGameHudTests.Hud(page);
            Assert.AreEqual("1", LivingArcadeGameHudTests.Element<TextBlock>(hud.HeroPanel, "Damage").Text);
            Assert.AreEqual("5/" + localizer.T("Arcade.SecondsShort"), LivingArcadeGameHudTests.Element<TextBlock>(hud.HeroPanel, "FireRate").Text);
            Assert.AreEqual("3", LivingArcadeGameHudTests.Element<TextBlock>(hud.HeroPanel, "Shots").Text);
            var pickup = LivingArcadeGameHudTests.Element<TextBlock>(hud.HeroPanel, "Pickup");
            StringAssert.Contains(pickup.Text, localizer.T("Arcade.Bonus.Picked"));
            StringAssert.Contains(pickup.Text, string.Format(localizer.T("Arcade.Bonus.Shots"), 2));
            Assert.IsFalse(pickup.Text.Contains(string.Format(localizer.T("Arcade.Bonus.Health"), 12)), "Full health must not claim a heal that did not happen.");
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-bonus-hud-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    });
}
