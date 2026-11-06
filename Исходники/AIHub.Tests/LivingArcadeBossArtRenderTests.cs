using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Size = System.Windows.Size;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed partial class LivingArcadeBossArtRenderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task RealDetailedLibraryFallbacksReachTheActualHostInBothViews() => ScenarioNavigationTests.Sta(() =>
    {
        var library = LivingArcadeLibrary.Load();
        var selected = new List<(LivingArcadeLevel Level, LivingArcadeBossArt Art)>();
        for (var seed = 1; seed < 1000 && selected.Count < 6; seed++)
        {
            var level = QuietBossLevel(seed);
            var art = LivingArcadeBossArtCatalog.CreateFallback(level, library);
            if (selected.All(item => item.Art.Visual.AssetId != art.Visual.AssetId)) selected.Add((level, art));
        }
        Assert.AreEqual(6, selected.Count, "The real library must offer more than one fixed boss body.");
        var pictures = new List<(string Label, BitmapSource Bitmap)>();
        foreach (var (level, expectedArt) in selected)
        {
            var originalOrdinary = JsonSerializer.Serialize(level.EnemyTypes);
            var simulation = new LivingArcadeSimulation(level, EarnedHero());
            using var host = new LivingArcadeGameHost(library);
            Silence(host); host.SetSimulation(simulation);
            Assert.IsNotNull(simulation.BossArt);
            Assert.AreEqual("library-fallback", simulation.BossArt.Source);
            Assert.AreEqual(expectedArt.Visual.AssetId, simulation.BossArt.Visual.AssetId);
            Assert.IsFalse(host.HasGeneratedBossArt);
            Assert.AreEqual(1, Sprites(host).PreparedCount, "The optional boss body must be prepared before its late spawn.");
            AwaitBoss(simulation);
            var boss = simulation.Snapshot.Enemies.Single(enemy => enemy.IsBoss);
            Assert.AreEqual(expectedArt.Visual.AssetId, boss.Visual?.AssetId);
            Assert.AreEqual(originalOrdinary, JsonSerializer.Serialize(level.EnemyTypes), "Ordinary art and combat definitions remain the same.");
            foreach (var view in new[] { LivingArcadeViewMode.TopDown, LivingArcadeViewMode.Perspective })
            {
                SelectView(host, level.Seed, view);
                var bitmap = Capture(host);
                Assert.IsFalse(Field<bool>(host, "_renderFailed"));
                var crop = BossCrop(bitmap, simulation.Snapshot, view);
                Assert.IsTrue(DistinctColors(crop) > 20, "The actual boss body must contain visible library detail: " + boss.Visual!.AssetId);
                Save(bitmap, $"library-{boss.Visual!.AssetId}-{view}.png", EvidenceDirectory, TestContext);
                pictures.Add(($"{expectedArt.TitleEn} · {view}", crop));
            }
            var beforeRelease = Capture(host);
            host.ReleaseGraphics();
            Assert.AreEqual(0, Sprites(host).PreparedCount);
            host.Start(); host.Stop();
            Assert.AreEqual(1, Sprites(host).PreparedCount);
            CollectionAssert.AreEqual(Pixels(beforeRelease), Pixels(Capture(host)), "Returning must rebuild the same illustrated boss.");
            host.SetSimulation(null); Assert.AreEqual(0, Sprites(host).PreparedCount);
        }
        Save(ContactSheet(pictures, 4), "six-library-bosses-both-views.png", EvidenceDirectory, TestContext);
    });

    [TestMethod]
    public Task DetailedModelStyleArtworkSurvivesAllThreeRealBossPhasesAndBothCameras() => ScenarioNavigationTests.Sta(() =>
    {
        var art = DetailedArtwork();
        var source = JsonSerializer.Serialize(art);
        var frames = FightAndCapture(art, EvidenceDirectory, "fixture", TestContext);
        Assert.AreEqual(6, frames.Count);
        Assert.AreEqual(source, JsonSerializer.Serialize(art), "Rendering must not rewrite the supplied illustration.");
        Save(ContactSheet(frames, 3), "fixture-three-phases-both-views.png", EvidenceDirectory, TestContext);
    });

    [TestMethod]
    public Task LateModelArtReplacementPreservesTheExactLiveFightAndOrdinaryDefinitions() => ScenarioNavigationTests.Sta(() =>
    {
        var level = QuietBossLevel(123);
        var ordinary = JsonSerializer.Serialize(level.EnemyTypes);
        var simulation = new LivingArcadeSimulation(level, EarnedHero());
        using var host = new LivingArcadeGameHost(LivingArcadeLibrary.Load());
        Silence(host); host.SetSimulation(simulation); AwaitBoss(simulation);
        for (var tick = 0; tick < 40 * 60 && simulation.Snapshot.BossPhase < 2; tick++) FollowBoss(simulation);
        Assert.AreEqual(2, simulation.Snapshot.BossPhase);
        Assert.IsTrue(simulation.Snapshot.Shots.Count > 0);
        SelectView(host, level.Seed, LivingArcadeViewMode.Perspective);
        var beforePicture = Capture(host);
        var beforeFight = FightState(simulation.Snapshot);
        var beforeHero = JsonSerializer.Serialize(simulation.ResultHero);
        var art = DetailedArtwork(); host.SetBossArt(art);
        Assert.IsTrue(host.HasGeneratedBossArt);
        Assert.AreEqual(beforeFight, FightState(simulation.Snapshot), "Changing art must not advance, stop or restart any battle state.");
        Assert.AreEqual(beforeHero, JsonSerializer.Serialize(simulation.ResultHero));
        Assert.AreEqual(ordinary, JsonSerializer.Serialize(level.EnemyTypes));
        Assert.AreSame(art.Visual, simulation.Snapshot.Enemies.Single(enemy => enemy.IsBoss).Visual);
        Assert.AreEqual(1, Sprites(host).PreparedCount, "The superseded fallback cache must be replaced, not retained.");
        var afterPicture = Capture(host);
        Assert.IsFalse(Field<bool>(host, "_renderFailed"));
        Assert.IsTrue(PixelDifference(beforePicture, afterPicture) > .002, "The supplied model drawing must visibly replace the old body.");
        Save(beforePicture, "live-fight-before-model-art.png", EvidenceDirectory, TestContext);
        Save(afterPicture, "live-fight-after-model-art.png", EvidenceDirectory, TestContext);
        host.ReleaseGraphics(); Assert.AreEqual(0, Sprites(host).PreparedCount);
        host.Start(); host.Stop(); Assert.AreEqual(1, Sprites(host).PreparedCount);
        Assert.IsTrue(host.HasGeneratedBossArt);
        host.Dispose(); Assert.AreEqual(0, Sprites(host).PreparedCount);
    });

    [TestMethod]
    public Task IllustratedBossRemainsVisibleInACompactViewport() => ScenarioNavigationTests.Sta(() =>
    {
        var level = QuietBossLevel(97); level.BossArt = DetailedArtwork();
        var simulation = new LivingArcadeSimulation(level, EarnedHero());
        using var host = new LivingArcadeGameHost(LivingArcadeLibrary.Load());
        Silence(host); host.SetSimulation(simulation); AwaitBoss(simulation);
        foreach (var view in new[] { LivingArcadeViewMode.TopDown, LivingArcadeViewMode.Perspective })
        {
            SelectView(host, level.Seed, view);
            var image = Capture(host, 360, 540);
            Assert.IsFalse(Field<bool>(host, "_renderFailed"));
            Assert.IsTrue(DistinctColors(BossCrop(image, simulation.Snapshot, view)) > 12);
            Save(image, $"compact-boss-{view}.png", EvidenceDirectory, TestContext);
        }
    });

    [TestMethod]
    public Task InvalidLateArtworkKeepsThePreparedBossAndLiveFight() => ScenarioNavigationTests.Sta(() =>
    {
        var level = QuietBossLevel(417);
        var simulation = new LivingArcadeSimulation(level, EarnedHero());
        using var host = new LivingArcadeGameHost(LivingArcadeLibrary.Load());
        Silence(host); host.SetSimulation(simulation); AwaitBoss(simulation);
        var before = FightState(simulation.Snapshot);
        var previous = simulation.BossArt;
        var picture = Capture(host);
        var invalid = DetailedArtwork(); invalid.Visual.Frames[0].Shapes[0].X = 999;
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => host.SetBossArt(invalid));
        Assert.AreSame(previous, simulation.BossArt);
        Assert.AreEqual(before, FightState(simulation.Snapshot));
        Assert.AreEqual(1, Sprites(host).PreparedCount);
        CollectionAssert.AreEqual(Pixels(picture), Pixels(Capture(host)));
        Assert.IsFalse(Field<bool>(host, "_renderFailed"));
    });

    [TestMethod]
    public Task NonBossStageKeepsOrdinaryProceduralArtAndDoesNotPrepareAnExtraBoss() => ScenarioNavigationTests.Sta(() =>
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(321);
        var ordinary = JsonSerializer.Serialize(level.EnemyTypes);
        var simulation = new LivingArcadeSimulation(level, EarnedHero());
        using var host = new LivingArcadeGameHost(LivingArcadeLibrary.Load());
        Silence(host); host.SetSimulation(simulation);
        Assert.IsNull(simulation.BossArt);
        Assert.AreEqual(0, Sprites(host).PreparedCount);
        host.SetBossArt(DetailedArtwork());
        Assert.IsNull(simulation.BossArt);
        Assert.AreEqual(ordinary, JsonSerializer.Serialize(simulation.EnemyDefinitions));
        _ = Capture(host); Assert.IsFalse(Field<bool>(host, "_renderFailed"));
    });

    internal static Task RenderGenuineBossArtAsync(LivingArcadeBossArt art, string outputDirectory) => ScenarioNavigationTests.Sta(() =>
    {
        var frames = FightAndCapture(art, outputDirectory, "genuine-model", null);
        Save(ContactSheet(frames, 3), "genuine-model-three-phases-both-views.png", outputDirectory, null);
    });

    private static string FightState(LivingArcadeSnapshot state) => JsonSerializer.Serialize(state with
    { Enemies = state.Enemies.Select(enemy => enemy with { Visual = null }).ToArray() });

    private static LivingArcadeSpriteRenderer Sprites(LivingArcadeGameHost host) => Field<LivingArcadeSpriteRenderer>(host, "_sprites");
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
