using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeCombatHudTests
{
    [TestMethod, DoNotParallelize]
    public Task WeaponAndHelperPickupsHaveDifferentVisibleGlyphs() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "weapon_pistol", "weapon_shotgun", "weapon_smg", "weapon_laser", "weapon_rocket", "helper" })
        {
            var pixels = Paint(host, "DrawPickupGlyph", dc => [dc, "bonus_" + kind, 18d], true);
            Assert.IsTrue(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 100) > 50, kind);
            Assert.IsTrue(signatures.Add(Signature(pixels)), kind + " must be recognisable separately.");
        }
    });

    [TestMethod, DoNotParallelize]
    public Task WeaponShotsKeepTheirOwnShapesWhenRewardChangesTheirColour() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { "pistol", "shotgun", "smg", "laser", "rockets", "helper_shot" })
        {
            var shot = new LivingArcadeEntityView(kind, .5, .5, .055, "#C86BE8", "circle", Hostile: false);
            var pixels = Paint(host, "DrawProjectile", dc => [dc, shot, 0d, 64d, 64d]);
            Assert.IsTrue(signatures.Add(Signature(pixels)), kind + " must have its own shot shape.");
            Assert.IsTrue(Enumerable.Range(0, pixels.Length / 4).Any(i =>
                pixels[i * 4] > 180 && pixels[i * 4 + 1] is > 50 and < 160 && pixels[i * 4 + 2] > 160),
                "The model reward colour must remain visible for " + kind);
        }
        var hostile = new LivingArcadeEntityView("laser", .5, .5, .055, "#C86BE8", "circle", Hostile: true);
        var roundHostile = new LivingArcadeEntityView("hostile", .5, .5, .055, "#C86BE8", "circle", Hostile: true);
        Assert.AreEqual(Signature(Paint(host, "DrawProjectile", dc => [dc, hostile, 0d, 64d, 64d])),
            Signature(Paint(host, "DrawProjectile", dc => [dc, roundHostile, 0d, 64d, 64d])),
            "Enemy shots must not become player lasers just because a type has that id.");
    });

    [TestMethod, DoNotParallelize]
    public Task RealHelpersAreDrawnWithHealthAndDisappearWhenDead() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        host.Measure(new Size(320, 320)); host.Arrange(new Rect(0, 0, 320, 320));
        var helper = new LivingArcadeEntityView("helper", .35, .75, .02, "#66CCFF", "triangle",
            HealthFraction: 1, Hostile: false) { Health = 24, MaxHealth = 24 };
        byte[] Scene(LivingArcadeEntityView? companion)
        {
            var snapshot = new LivingArcadeSnapshot { Companions = companion is null ? [] : [companion] };
            return Paint(host, "Draw", dc => [dc, snapshot], size: 320);
        }
        var empty = Signature(Scene(null));
        Assert.AreNotEqual(empty, Signature(Scene(helper)), "Live helpers must actually be drawn by the field.");
        Assert.AreNotEqual(Signature(Scene(helper)), Signature(Scene(helper with { HealthFraction = .25, Health = 6 })),
            "Helper health must change its visible bar.");
        Assert.AreEqual(empty, Signature(Scene(helper with { HealthFraction = 0, Health = 0 })),
            "A helper has one life and must leave no immortal decorative ship behind.");
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void PickupTextNamesWeaponChangesAndOneLifeHelpers(string language)
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        foreach (var kind in new[] { "pistol", "shotgun", "smg" })
        {
            var text = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false) { Kind = "weapon_" + kind });
            StringAssert.Contains(text, localizer.T("Arcade.Weapon." + kind));
            StringAssert.Contains(text, localizer.T("Arcade.Weapon.UntilReplaced"));
            Assert.IsFalse(text.Contains(localizer.T("Arcade.Bonus.Maximum")));
        }
        var laser = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false) { Kind = "weapon_laser", DurationSeconds = 8 });
        StringAssert.Contains(laser, "8 " + localizer.T("Arcade.SecondsShort"));
        Assert.IsFalse(laser.Contains(localizer.T("Arcade.Weapon.UntilReplaced")));
        var helper = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false) { Kind = "helper", CompanionAdded = true });
        StringAssert.Contains(helper, localizer.T("Arcade.Companion.OneLife"));
        Assert.IsFalse(helper.Contains(localizer.T("Arcade.Bonus.Maximum")));
        var capped = LivingArcadeHud.Pickup(localizer.T, new(0, 0, 0, 0, false) { Kind = "helper" });
        StringAssert.Contains(capped, localizer.T("Arcade.Powerup.helper") + ": " + localizer.T("Arcade.Bonus.Maximum"));
        Assert.IsFalse(capped.Contains(localizer.T("Arcade.Companion.OneLife")));
    }

    [TestMethod, DoNotParallelize]
    [DataRow("ru")]
    [DataRow("en")]
    public Task ActualHudNamesWeaponRewardDurationAndEachHelpersActualHealth(string language) => ScenarioNavigationTests.Sta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-combat-hud-" + Guid.NewGuid().ToString("N"));
        try
        {
            var localizer = new LocalizationService(); localizer.Load(language);
            var store = new LivingArcadeRunStore(directory);
            using var generation = new LivingArcadeGeneration(new UserContextService(new(), new()));
            using var page = new LivingArcadeControl(localizer.T, store, generation,
                new("unused", "unused"), () => language, default);
            var type = typeof(LivingArcadeControl);
            LivingArcadeGameHudTests.StartFixtureRun(page, store);
            var active = new LivingArcadeActiveReward
            {
                RemainingLevels = 2,
                Reward = new LivingArcadeBossReward { TitleRu = "Косой веер", TitleEn = "Side fan",
                    DescriptionRu = "Пули расходятся в стороны.", DescriptionEn = "Shots spread sideways.", DurationLevels = 2 }
            };
            var snapshot = new LivingArcadeSnapshot { WeaponKind = "laser", WeaponSeconds = 5.1, BossReward = active,
                Companions = [new("helper1", .4, .7, .02, "#66CCFF", "triangle", .5, false) { Health = 7, MaxHealth = 14 },
                    new("helper2", .6, .7, .02, "#66CCFF", "triangle", .75, false) { Health = 18, MaxHealth = 24 }] };
            type.GetMethod("OnGameFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [snapshot]);
            var hud = LivingArcadeGameHudTests.Hud(page);
            var text = LivingArcadeGameHudTests.Text(hud.HeroPanel);
            StringAssert.Contains(text, localizer.T("Arcade.Weapon.laser") + " 6 " + localizer.T("Arcade.SecondsShort"));
            StringAssert.Contains(text, "7/14");
            StringAssert.Contains(text, "18/24");
            StringAssert.Contains(text, language == "en" ? "Side fan" : "Косой веер");
            StringAssert.Contains(text, string.Format(localizer.T("Arcade.BossReward.Active"),
                language == "en" ? "Side fan" : "Косой веер", 2));
            var description = language == "en" ? active.Reward.DescriptionEn : active.Reward.DescriptionRu;
            var reward = LivingArcadeGameHudTests.Element<TextBlock>(hud.HeroPanel, "BossReward");
            var tooltip = (string)reward.ToolTip;
            var executableDetails = LivingArcadeHud.BossRewardDetails(localizer.T, active.Reward);
            StringAssert.Contains(tooltip, executableDetails);
            StringAssert.Contains(tooltip, Environment.NewLine + Environment.NewLine + localizer.T("Arcade.BossReward.ModelDescription"));
            Assert.IsTrue(tooltip.IndexOf(executableDetails, StringComparison.Ordinal)
                < tooltip.IndexOf(localizer.T("Arcade.BossReward.ModelDescription"), StringComparison.Ordinal),
                "Executable properties must precede and stay separate from the model's prose.");
            Assert.IsTrue(tooltip.EndsWith(description, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("Arcade."));
            type.GetField("_lastHud", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, DateTime.MinValue);
            type.GetMethod("OnGameFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page,
                [new LivingArcadeSnapshot()]);
            Assert.AreEqual(Visibility.Collapsed, reward.Visibility, "An expired reward must disappear from the actual panel.");
            Assert.IsTrue(reward.ToolTip is null or "", "An expired reward must not leave a stale description.");
        }
        finally
        {
            if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(directory).StartsWith("arcade-combat-hud-", StringComparison.Ordinal)) Directory.Delete(directory, true);
        }
    });

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public void RewardDetailsFollowExecutableFieldsAndKeepModelClaimsSeparate(string language)
    {
        var localizer = new LocalizationService(); localizer.Load(language);
        var reward = RewardFixture("fan");
        var original = System.Text.Json.JsonSerializer.Serialize(reward);
        var details = LivingArcadeHud.BossRewardDetails(localizer.T, reward);
        var rewrittenProse = reward with { TitleRu = "Другое название", TitleEn = "Another title",
            DescriptionRu = "Все снаряды становятся быстрее и гремят, всегда вылетают пять.",
            DescriptionEn = "All projectiles get faster and thunderous, always firing five." };
        Assert.AreEqual(details, LivingArcadeHud.BossRewardDetails(localizer.T, rewrittenProse));
        StringAssert.Contains(details, string.Format(localizer.T("Arcade.BossReward.Damage"), 50));
        StringAssert.Contains(details, string.Format(localizer.T("Arcade.BossReward.FireRate"), 25));
        StringAssert.Contains(details, localizer.T("Arcade.BossReward.Pattern.fan"));
        StringAssert.Contains(details, localizer.T("Arcade.BossReward.NoPierce"));
        StringAssert.Contains(details, string.Format(localizer.T("Arcade.BossReward.Duration"), 2));
        Assert.IsFalse(details.Contains("Arcade."));
        Assert.AreEqual(original, System.Text.Json.JsonSerializer.Serialize(reward), "The raw model description and provenance stay intact.");

        foreach (var pattern in new[] { "straight", "wave", "returning" })
        {
            var actual = reward with { ShotPattern = pattern, Pierce = 2 };
            var text = LivingArcadeHud.BossRewardDetails(localizer.T, actual);
            StringAssert.Contains(text, localizer.T("Arcade.BossReward.Pattern." + pattern));
            StringAssert.Contains(text, string.Format(localizer.T("Arcade.BossReward.Pierce"), 2));
            Assert.IsFalse(text.Contains(localizer.T("Arcade.BossReward.Pattern.fan")));
        }
    }

    [TestMethod]
    [DataRow("straight", 1)]
    [DataRow("fan", 3)]
    public void UnsupportedDescriptionClaimsDoNotChangeProjectileCountSpeedOrDamage(string pattern, int expectedVolley)
    {
        var reward = RewardFixture(pattern);
        var prose = reward with { DescriptionRu = "Скорость пуль удвоена, всегда пять снарядов и громкие взрывы.",
            DescriptionEn = "Double projectile speed, always five projectiles and loud explosions." };
        var level = LivingArcadeLevelPolicy.CreateFirst(17) with { Number = 2, MiniEvents = [] };
        LivingArcadeSimulation Create(LivingArcadeBossReward value) => new(level, new()
        {
            ActiveBossReward = new() { Reward = value, StartLevel = 2, RemainingLevels = 2 },
            PowerupBalanceVersion = LivingArcadeHeroBalance.Version
        });
        var original = Create(reward); var exaggerated = Create(prose);
        original.Advance(1d / 60, default); exaggerated.Advance(1d / 60, default);
        Assert.AreEqual(expectedVolley, original.Snapshot.Shots.Count(s => !s.Hostile));
        for (var tick = 0; tick < 90; tick++)
        {
            original.Advance(1d / 60, default); exaggerated.Advance(1d / 60, default);
            Assert.AreEqual(original.Snapshot.ProjectileDamage, exaggerated.Snapshot.ProjectileDamage);
            Assert.AreEqual(original.Snapshot.FireRate, exaggerated.Snapshot.FireRate);
            Assert.AreEqual(original.Snapshot.SpreadShots, exaggerated.Snapshot.SpreadShots);
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(original.Snapshot.Shots),
                System.Text.Json.JsonSerializer.Serialize(exaggerated.Snapshot.Shots),
                "Only numeric reward fields can affect shot types, positions, speed and colour.");
        }
    }

    private static LivingArcadeBossReward RewardFixture(string pattern) => LivingArcadeGeneration.ParseBossReward($$"""
        {"titleRu":"Веер дороги","titleEn":"Road fan","descriptionRu":"Снаряды расходятся в две стороны, давая пять снарядов.",
        "descriptionEn":"Projectiles split in two directions, giving five projectiles.","durationLevels":2,
        "damageMultiplier":1.5,"fireRateMultiplier":1.25,"shotPattern":"{{pattern}}","pierce":0,"colorHex":"#C86BE8"}
        """);

    private static byte[] Paint(LivingArcadeGameHost host, string method, Func<DrawingContext, object[]> arguments,
        bool centered = false, int size = 64)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (centered) dc.PushTransform(new TranslateTransform(size / 2d, size / 2d));
            typeof(LivingArcadeGameHost).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, arguments(dc));
            if (centered) dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[size * size * 4]; bitmap.CopyPixels(pixels, size * 4, 0); return pixels;
    }

    private static string Signature(byte[] pixels) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pixels));
}
