using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.LivingArcade;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePowerupHudTests
{
    [TestMethod, DoNotParallelize]
    public Task EveryPickupKindPaintsADistinctReadableGlyph() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var paint = typeof(LivingArcadeGameHost).GetMethod("DrawPickupGlyph", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "power", "rapid", "spread", "shield", "repair", "weapon", "cadence", "vitality", "time_scope", "jam", "drag", "fragile" })
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new TranslateTransform(32, 32));
                paint.Invoke(host, [dc, (kind is "jam" or "drag" or "fragile" ? "negative_" : "bonus_") + kind, 18d]);
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var pixels = new byte[64 * 64 * 4]; bitmap.CopyPixels(pixels, 64 * 4, 0);
            Assert.IsTrue(Enumerable.Range(0, 64 * 64).Count(i => pixels[i * 4 + 3] > 100) > 50, kind + " must be visible.");
            Assert.IsTrue(signatures.Add(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pixels))),
                kind + " must not reuse another pickup's glyph.");
        }
        Assert.AreEqual(12, signatures.Count);
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void PickupTextSeparatesTimedEffectsFromActualPermanentGains(string language)
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var shield = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false)
            { Kind = "shield", DurationSeconds = 6 });
        StringAssert.Contains(shield, localizer.T("Arcade.Powerup.shield"));
        StringAssert.Contains(shield, "6 " + localizer.T("Arcade.SecondsShort"));
        Assert.IsFalse(shield.Contains(localizer.T("Arcade.Bonus.Maximum")));
        var damage = LivingArcadeHud.Pickup(localizer.T, new(.02, 0, 0, 0, false) { Kind = "weapon" });
        StringAssert.Contains(damage, string.Format(localizer.T("Arcade.Bonus.Damage"), .02));
        StringAssert.Contains(damage, localizer.T("Arcade.Bonus.Permanent"));
        Assert.IsFalse(damage.Contains(string.Format(localizer.T("Arcade.Bonus.Health"), 12)));
        var vitality = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false) { Kind = "vitality", MaxHealthGain = 2 });
        StringAssert.Contains(vitality, string.Format(localizer.T("Arcade.Bonus.MaxHealth"), 2));
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void NegativePickupHasItsOwnNameAndNeverClaimsPositiveUpgrades(string language)
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        foreach (var kind in new[] { "jam", "drag", "fragile" })
        {
            var text = LivingArcadeHud.Pickup(localizer.T, new(0, -.5, 0, 0, false)
                { Kind = kind, DurationSeconds = 6, IsNegative = true });
            StringAssert.Contains(text, localizer.T("Arcade.Bonus.Negative"));
            StringAssert.Contains(text, localizer.T("Arcade.Powerup." + kind));
            Assert.IsFalse(text.Contains(localizer.T("Arcade.Bonus.Picked")));
            Assert.IsFalse(text.Contains(localizer.T("Arcade.Bonus.Maximum")));
            Assert.IsFalse(text.Contains("Arcade."));
        }
        Assert.AreEqual("1 242", LivingArcadeHud.KillCount(1242));
        Assert.AreEqual("10 000", LivingArcadeHud.KillCount(10000));
    }

    [TestMethod, DoNotParallelize]
    public Task ExperienceSavesDuringPlayAndAtEveryExitBoundaryWithoutSavingTemporaryBonuses() => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-powerup-hud-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory); var run = store.Create("XP checkpoint", 71);
            var localizer = new LocalizationService(); localizer.Load("ru");
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation, new("unused", "unused"), () => "ru", default);
            var type = typeof(LivingArcadeControl);
            void Set(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
            void Invoke(string name, params object[] values) => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, values);
            void GiveKills(long count) => Set("_simulation", new LivingArcadeSimulation(LivingArcadeLevelPolicy.CreateFirst(71), new() { TotalKills = count }));
            Set("_playing", true); Set("_runId", run.Id); Set("_lastExperienceSave", DateTime.UtcNow); GiveKills(99);
            Invoke("OnGameFrame", new LivingArcadeSnapshot());
            Assert.AreEqual(0L, store.Load(run.Id)!.EntryState.TotalKills, "Live checkpoints are throttled to once per second.");
            Set("_lastExperienceSave", DateTime.UtcNow.AddSeconds(-2)); Invoke("OnGameFrame", new LivingArcadeSnapshot());
            Assert.AreEqual(99L, store.Load(run.Id)!.EntryState.TotalKills);
            var revision = store.Load(run.Id)!.Revision;
            Invoke("SaveExperience", true);
            Assert.AreEqual(revision, store.Load(run.Id)!.Revision, "Unchanged experience must not rewrite the run.");
            GiveKills(100); Invoke("PauseGame"); Assert.AreEqual(100L, store.Load(run.Id)!.EntryState.TotalKills);
            GiveKills(101); page.SuspendForNavigation(); Assert.AreEqual(101L, store.Load(run.Id)!.EntryState.TotalKills);
            GiveKills(102); Invoke("ShowMenu"); Assert.AreEqual(102L, store.Load(run.Id)!.EntryState.TotalKills);
            Set("_playing", true); GiveKills(103); page.Dispose();
            var saved = store.Load(run.Id)!;
            Assert.AreEqual(103L, saved.EntryState.TotalKills);
            Assert.AreEqual(run.EntryState.WeaponPower, saved.EntryState.WeaponPower);
            Assert.AreEqual(run.EntryState.FireRate, saved.EntryState.FireRate);
            Assert.AreEqual(run.EntryState.SpreadShots, saved.EntryState.SpreadShots);
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-powerup-hud-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    });

    [TestMethod, DoNotParallelize]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ActualHudShowsExperienceTimersAndBossHealthOutsideTheField(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-powerup-hud-" + Guid.NewGuid().ToString("N"));
        try
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var store = new LivingArcadeRunStore(directory);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            LivingArcadeGameHudTests.StartFixtureRun(page, store);
            var snapshot = new LivingArcadeSnapshot { GameLevel = 6, CharacterLevel = 5,
                KillsTowardNextLevel = 1242, KillsRequiredForNextLevel = 10000, TempoMultiplier = 1.15,
                PowerupEffect = "spread", PowerupSeconds = 4.2, NegativeEffect = "drag", NegativeSeconds = 2.1,
                BossPhase = 2, BossHealth = 51.4, BossMaxHealth = 90, Progress = 140, DurationSeconds = 120 };
            typeof(LivingArcadeControl).GetMethod("OnGameFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [snapshot]);
            var hud = LivingArcadeGameHudTests.Hud(page);
            var visibleText = string.Join("\n", LivingArcadeGameHudTests.Text(hud.HeroPanel),
                LivingArcadeGameHudTests.Text(hud.StageHeader), LivingArcadeGameHudTests.Text(hud.Abilities));
            StringAssert.Contains(visibleText, "1 242/10 000");
            var stageTime = LivingArcadeGameHudTests.Element<TextBlock>(hud.StageHeader, "StageTime").Text.Replace(" ", "");
            StringAssert.Contains(stageTime, "120/120");
            Assert.IsFalse(stageTime.Contains("140/120"), "The completed road must not look unfinished while the boss fight continues.");
            StringAssert.Contains(visibleText, localizer.T("Arcade.Powerup.spread") + " 5 " + localizer.T("Arcade.SecondsShort"));
            StringAssert.Contains(visibleText, localizer.T("Arcade.Powerup.drag") + " 3 " + localizer.T("Arcade.SecondsShort"));
            StringAssert.Contains(visibleText, string.Format(localizer.T("Arcade.BossStats"), 2, 52, 90));
            Assert.AreEqual(Visibility.Visible, LivingArcadeGameHudTests.Element<FrameworkElement>(hud.StageHeader, "BossBar").Visibility);
            StringAssert.Contains(LivingArcadeGameHudTests.Element<TextBlock>(hud.StageHeader, "Boss").Text, "2/3");
            Assert.IsFalse(visibleText.Contains("Arcade."));
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-powerup-hud-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    });
}
