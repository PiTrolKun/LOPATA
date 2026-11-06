using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LivingArcadeBackgroundTests
{
    private static readonly string[] Kinds = ["harbor", "forest", "desert", "ice", "city", "alien"];
    private static readonly string[] Labels = ["Промышленные доки", "Лесные руины", "Пустынный каньон",
        "Полярная база", "Ночной город", "Чужой мир"];
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void LocationPersistsForThreeStagesAndAlwaysChangesAfterTheBoss()
    {
        foreach (var seed in new[] { 0, 1, -1, 1731, int.MaxValue, int.MinValue })
        {
            string? previous = null;
            var firstCycle = new HashSet<string>();
            for (var chapter = 1; chapter <= 24; chapter++)
            {
                var firstLevel = (chapter - 1) * 3 + 1;
                var first = LivingArcadeSceneryPolicy.Create(seed, firstLevel);
                Assert.AreEqual(chapter, first.Chapter);
                CollectionAssert.Contains(Kinds, first.Kind);
                Assert.AreNotEqual(previous, first.Kind, "A boss victory must lead to a different place, including a cycle boundary.");
                previous = first.Kind;
                if (chapter <= Kinds.Length) firstCycle.Add(first.Kind);
                for (var offset = 0; offset < 3; offset++)
                {
                    var stage = LivingArcadeSceneryPolicy.Create(seed, firstLevel + offset);
                    Assert.AreEqual(first.Kind, stage.Kind, "Stages before one boss share their location.");
                    Assert.AreEqual(first.Seed, stage.Seed, "The chapter's landmarks must remain related.");
                    Assert.AreEqual(offset, stage.Variant);
                    Assert.AreEqual(stage, LivingArcadeSceneryPolicy.Create(seed, firstLevel + offset),
                        "Reopening a stage must reproduce its location without another random choice.");
                }
            }
            Assert.AreEqual(Kinds.Length, firstCycle.Count, "A complete location cycle must reach every built-in place.");
        }
    }

    [TestMethod]
    public void DerivingSceneryFromAnExistingRunDoesNotRewriteItsManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "arcade-background-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LivingArcadeRunStore(directory);
            var run = store.Create("Existing run", 73);
            var before = Files(directory);
            var expected = Enumerable.Range(1, 18).Select(level => LivingArcadeSceneryPolicy.Create(run.Seed, level)).ToArray();
            var reopened = new LivingArcadeRunStore(directory).Load(run.Id)!;
            var actual = Enumerable.Range(1, 18).Select(level => LivingArcadeSceneryPolicy.Create(reopened.Seed, level)).ToArray();
            CollectionAssert.AreEqual(expected, actual);
            var after = Files(directory);
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
            foreach (var name in before.Keys)
                CollectionAssert.AreEqual(before[name], after[name], "Scenery is derived without rewriting existing saves: " + name);
            Assert.AreEqual(run.Revision, reopened.Revision);
        }
        finally { DeleteFixture(directory); }
    }

    [TestMethod]
    public Task EveryPlaceIsDetailedDistinctReproducibleAndScrollsWithoutHidingTheCorridor() => ScenarioNavigationTests.Sta(() =>
    {
        var frames = new List<(string Kind, RenderTargetBitmap Bitmap)>();
        var pictures = new Dictionary<string, byte[]>();
        var renderer = new LivingArcadeBackground();
        foreach (var kind in Kinds)
        {
            renderer.Prepare(kind, 4173, 0);
            Assert.IsTrue(renderer.IsPrepared);
            Assert.AreEqual(3, renderer.CachedDrawingCount, "The background retains three reusable vector strips, not per-frame scenes.");
            var still = Render(renderer, 17);
            var pixels = Pixels(still);
            Assert.IsTrue(ColorCount(pixels) >= 18, kind + " needs actual terrain/landmark detail, not one flat color.");
            AssertMutedCorridor(still, kind);
            renderer.Prepare(kind, 4173, 0);
            CollectionAssert.AreEqual(pixels, Pixels(Render(renderer, 17)), kind + " must survive a retry without visual reshuffling.");
            var moved = Pixels(Render(renderer, 18.3));
            Assert.IsTrue(Difference(pixels, moved) > .01, kind + " must visibly scroll while the player advances.");
            renderer.Prepare(kind, 4173, 1);
            Assert.IsTrue(Difference(pixels, Pixels(Render(renderer, 17))) > .01,
                kind + " needs a local stage variation inside the same location.");
            frames.Add((kind, still)); pictures.Add(kind, pixels);
            Save(still, kind + ".png");
        }
        for (var first = 0; first < Kinds.Length; first++)
        for (var second = first + 1; second < Kinds.Length; second++)
            Assert.IsTrue(Difference(pictures[Kinds[first]], pictures[Kinds[second]]) > .15,
                $"Different locations must be distinguishable on screen: {Kinds[first]}, {Kinds[second]}.");
        Save(ContactSheet(frames), "all-locations.png");
        renderer.Clear();
        Assert.IsFalse(renderer.IsPrepared);
        Assert.AreEqual(0, renderer.CachedDrawingCount);
        renderer.Prepare("harbor", 4173, 0);
        CollectionAssert.AreEqual(pictures["harbor"], Pixels(Render(renderer, 17)), "Released vector resources must be safely rebuildable.");
    });

    [TestMethod]
    public Task ScrollingReusesItsDrawingsWithABoundedAllocationBudget() => ScenarioNavigationTests.Sta(() =>
    {
        var renderer = new LivingArcadeBackground();
        renderer.Prepare("forest", 873, 2);
        var visual = new DrawingVisual();
        void Draw(double progress)
        {
            using var dc = visual.RenderOpen();
            renderer.Draw(dc, 800, 600, 100, 600, progress);
        }
        for (var frame = 0; frame < 20; frame++) Draw(frame / 60d);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 400; frame++) Draw(1_000_000 + frame / 60d);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.WriteLine($"400 scrolling frames allocated {allocated:N0} managed bytes.");
        Assert.IsTrue(allocated < 32L * 1024 * 1024,
            "Scrolling must reuse the expensive prepared drawings rather than regenerate every landmark per frame.");
        Assert.AreEqual(3, renderer.CachedDrawingCount, "Long play must not retain a new background for every frame.");
        renderer.Clear();
        Draw(1_000_001); // Unloading may still be followed by one queued frame.
        Assert.AreEqual(0, renderer.CachedDrawingCount);
    });

    [TestMethod]
    public Task ActualGameHostUsesTheSelectedLocationAtWideAndCompactSizes() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        var level = LivingArcadeLevelPolicy.CreateFirst(73);
        level.Waves = [new() { At = 100, EnemyId = "scout", Count = 1 }]; level.MiniEvents = [];
        var simulation = new LivingArcadeSimulation(level, new());
        // The legacy geometry assertion must explicitly select the top-down member of this chapter.
        var topDownLevel = Enumerable.Range(4, 3).First(number =>
            LivingArcadePresentationPolicy.ModeFor(73, number) == LivingArcadeViewMode.TopDown);
        host.SetBackgroundScene(73, topDownLevel);
        host.SetSimulation(simulation);
        var first = host.BackgroundScene;
        Assert.AreEqual(LivingArcadeSceneryPolicy.Create(73, topDownLevel), first);
        foreach (var (width, height) in new[] { (800, 600), (360, 540) })
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            var bitmap = Capture(host, width, height);
            var pixels = Pixels(bitmap);
            Assert.IsTrue(ColorCount(pixels) >= 18, "The real host must draw the prepared location.");
            var fieldHeight = Math.Min(width * .88, height);
            var player = simulation.Snapshot;
            var expectedX = (width - fieldHeight) / 2 + player.PlayerX * fieldHeight;
            var expectedY = (height - fieldHeight) / 2 + player.PlayerY * fieldHeight;
            var cyan = new List<Point>();
            for (var y = Math.Max(0, (int)expectedY - 45); y < Math.Min(height, expectedY + 45); y++)
            for (var x = Math.Max(0, (int)expectedX - 45); x < Math.Min(width, expectedX + 45); x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] == 0xDF && pixels[i + 1] == 0xD8 && pixels[i + 2] == 0x9A) cyan.Add(new(x, y));
            }
            Assert.IsTrue(cyan.Count > 15, "The hero must remain visible over terrain.");
            Assert.IsTrue(Math.Abs(cyan.Average(p => p.X) - expectedX) < 6,
                "Changing scenery must not move the simulation's horizontal center.");
            Save(bitmap, $"actual-host-{width}.png");
        }
        host.SetBackgroundScene(73, 7);
        Assert.AreNotEqual(first.Kind, host.BackgroundScene.Kind, "The host must pick the next place after another boss.");
        Assert.AreEqual(0, simulation.Snapshot.Progress);
        Assert.AreEqual(100d, simulation.Snapshot.Health);
    });

    private static Dictionary<string, byte[]> Files(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), path => SHA256.HashData(File.ReadAllBytes(path)));

    private static RenderTargetBitmap Render(LivingArcadeBackground renderer, double progress)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) renderer.Draw(dc, 600, 500, 100, 400, progress);
        return Capture(visual, 600, 500);
    }

    private static RenderTargetBitmap Capture(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }

    private static int ColorCount(byte[] pixels)
    {
        var colors = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 16)
            colors.Add((pixels[i] / 8 << 10) | (pixels[i + 1] / 8 << 5) | pixels[i + 2] / 8);
        return colors.Count;
    }

    private static double Difference(byte[] first, byte[] second)
    {
        Assert.AreEqual(first.Length, second.Length);
        var changed = 0;
        for (var i = 0; i < first.Length; i += 4)
            if (Math.Abs(first[i] - second[i]) + Math.Abs(first[i + 1] - second[i + 1]) + Math.Abs(first[i + 2] - second[i + 2]) > 12) changed++;
        return changed / (first.Length / 4d);
    }

    private static void AssertMutedCorridor(BitmapSource bitmap, string kind)
    {
        var pixels = Pixels(bitmap); var dark = 0; var count = 0;
        for (var y = 0; y < bitmap.PixelHeight; y += 2)
        for (var x = 140; x < 460; x += 2)
        {
            var i = (y * bitmap.PixelWidth + x) * 4;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 125) dark++;
            count++;
        }
        Assert.IsTrue(dark >= count * .85, kind + " must reserve bright contrast for enemies and projectiles.");
    }

    private static RenderTargetBitmap ContactSheet(IReadOnlyList<(string Kind, RenderTargetBitmap Bitmap)> frames)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1800, 1080));
            for (var i = 0; i < frames.Count; i++)
            {
                var x = i % 3 * 600; var y = i / 3 * 540;
                dc.DrawImage(frames[i].Bitmap, new Rect(x, y + 40, 600, 500));
                var title = new FormattedText(Labels[Array.IndexOf(Kinds, frames[i].Kind)], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 23, Brushes.White, 1);
                dc.DrawText(title, new Point(x + 16, y + 5));
            }
        }
        return Capture(visual, 1800, 1080);
    }

    private void Save(BitmapSource bitmap, string name)
    {
        // The test process working directory can vary; the source location is the stable workspace anchor.
        var directory = Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "_tmp", "arcade-background-20261107", "render");
        Directory.CreateDirectory(directory);
        var file = Path.GetFullPath(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(file)) encoder.Save(output);
        TestContext.AddResultFile(file);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("arcade-background-save-", StringComparison.Ordinal)) Directory.Delete(full, true);
    }
}
