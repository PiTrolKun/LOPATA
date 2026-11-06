using System.Globalization;
using System.Reflection;
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
public sealed class LivingArcadePerspectiveRenderTests
{
    private static readonly string[] Kinds = ["harbor", "forest", "desert", "ice", "city", "alien"];
    private static readonly string[] Labels = ["Промышленные доки", "Лесные руины", "Пустынный каньон",
        "Полярная база", "Ночной город", "Чужой мир"];
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AllPerspectivePlacesAreDistinctReproducibleDetailedAndScroll() => ScenarioNavigationTests.Sta(() =>
    {
        var renderer = new LivingArcadePerspectiveBackground();
        var frames = new List<(string Label, BitmapSource Bitmap)>();
        var pictures = new List<byte[]>();
        foreach (var kind in Kinds)
        {
            renderer.Prepare(kind, 4173, 0);
            Assert.IsTrue(renderer.IsPrepared);
            Assert.IsTrue(renderer.CachedDrawingCount is > 0 and <= 12, "Only a bounded set of reusable scenery drawings may be retained.");
            var still = Render(renderer, 17);
            var pixels = Pixels(still);
            Assert.IsTrue(ColorCount(pixels) >= 18, kind + " needs terrain and landmarks rather than a flat trapezoid.");
            AssertReadableCorridor(still, kind);
            renderer.Prepare(kind, 4173, 0);
            CollectionAssert.AreEqual(pixels, Pixels(Render(renderer, 17)), "Reopening the same stage must not change the place: " + kind);
            Assert.IsTrue(Difference(pixels, Pixels(Render(renderer, 18.3))) > .01, "The ground must visibly approach the player: " + kind);
            renderer.Prepare(kind, 4173, 1);
            Assert.IsTrue(Difference(pixels, Pixels(Render(renderer, 17))) > .005, "Successive stages need local variation: " + kind);
            frames.Add((Labels[Array.IndexOf(Kinds, kind)], still)); pictures.Add(pixels);
            Save(still, "perspective-" + kind + ".png");
        }
        for (var first = 0; first < pictures.Count; first++)
        for (var second = first + 1; second < pictures.Count; second++)
            Assert.IsTrue(Difference(pictures[first], pictures[second]) > .12,
                $"Perspective places must be visually distinguishable: {Kinds[first]} / {Kinds[second]}.");
        Save(ContactSheet(frames, 3, 600, 500), "perspective-all-locations.png");
        renderer.Clear();
        Assert.IsFalse(renderer.IsPrepared);
        Assert.AreEqual(0, renderer.CachedDrawingCount);
        _ = Render(renderer, 1_000_001); // A queued render after unloading remains safe.
        renderer.Prepare("harbor", 4173, 0);
        CollectionAssert.AreEqual(pictures[0], Pixels(Render(renderer, 17)), "Released scenery must be rebuildable without changing its identity.");
    });

    [TestMethod]
    public Task PerspectiveScrollingHasBoundedCachesAndFrameAllocation() => ScenarioNavigationTests.Sta(() =>
    {
        var renderer = new LivingArcadePerspectiveBackground();
        renderer.Prepare("forest", 873, 2);
        var count = renderer.CachedDrawingCount;
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
        TestContext.WriteLine($"400 perspective frames allocated {allocated:N0} managed bytes; retained drawings: {count}.");
        Assert.IsTrue(allocated < 64L * 1024 * 1024, "Perspective scrolling must reuse prepared landmarks instead of rebuilding full locations every frame.");
        Assert.AreEqual(count, renderer.CachedDrawingCount);
        renderer.Clear(); Draw(1_000_001);
        Assert.AreEqual(0, renderer.CachedDrawingCount);
    });

    [TestMethod]
    public Task RecyclingGroundRowsDoesNotAbruptlyChangeTheirPattern() => ScenarioNavigationTests.Sta(() =>
    {
        var renderer = new LivingArcadePerspectiveBackground();
        foreach (var kind in Kinds)
        {
            renderer.Prepare(kind, 4173, 0);
            foreach (var row in new[] { 7, 32 })
            {
                var boundary = row * .07 / .095;
                // Isolate road-row recycling from independently moving roadside billboards.
                // WPF's subpixel coverage changes on those vector edges even away from any row boundary.
                var before = Pixels(RenderTerrain(renderer, boundary - .0001));
                var after = Pixels(RenderTerrain(renderer, boundary + .0001));
                var difference = Difference(before, after);
                TestContext.WriteLine($"{kind} row {row} recycling changed {difference:P4} of pixels.");
                Assert.IsTrue(difference < .001,
                    "A recycled road row must keep its world pattern, rather than visibly flicker at each modulo boundary: " + kind);
            }
        }
        renderer.Clear();
    });

    [TestMethod]
    public Task HostReleasesTheInactiveViewCacheAndBothViewsOnUnloadAndDispose() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var flat = (LivingArcadeBackground)typeof(LivingArcadeGameHost)
            .GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var perspective = (LivingArcadePerspectiveBackground)typeof(LivingArcadeGameHost)
            .GetField("_perspectiveBackground", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        foreach (var view in new[] { LivingArcadeViewMode.Perspective, LivingArcadeViewMode.TopDown,
            LivingArcadeViewMode.Perspective, LivingArcadeViewMode.TopDown })
        {
            var level = Enumerable.Range(1, 3).First(number => LivingArcadePresentationPolicy.ModeFor(73, number) == view);
            host.SetBackgroundScene(73, level);
            Assert.AreEqual(view == LivingArcadeViewMode.TopDown, flat.IsPrepared);
            Assert.AreEqual(view == LivingArcadeViewMode.Perspective, perspective.IsPrepared);
            Assert.IsTrue((flat.CachedDrawingCount == 0) != (perspective.CachedDrawingCount == 0),
                "Switching view must release the old prepared location rather than retaining both renderers.");
        }
        host.ReleaseGraphics();
        Assert.AreEqual(0, flat.CachedDrawingCount); Assert.AreEqual(0, perspective.CachedDrawingCount);
        host.SetBackgroundScene(73, Enumerable.Range(1, 3)
            .First(number => LivingArcadePresentationPolicy.ModeFor(73, number) == LivingArcadeViewMode.Perspective));
        Assert.IsTrue(perspective.IsPrepared, "Returning to the game must be able to prepare released scenery again.");
        host.Dispose(); host.Dispose();
        Assert.AreEqual(0, flat.CachedDrawingCount); Assert.AreEqual(0, perspective.CachedDrawingCount);
    });

    [TestMethod]
    public Task RealHostShowsBothViewsAtWideAndCompactSizesWithoutChangingSimulation() => ScenarioNavigationTests.Sta(() =>
    {
        const int seed = 73;
        using var host = new LivingArcadeGameHost();
        host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        var level = LivingArcadeLevelPolicy.CreateFirst(seed);
        level.Waves = [new() { At = 100, EnemyId = "scout", Count = 1 }]; level.MiniEvents = [];
        var simulation = new LivingArcadeSimulation(level, new());
        host.SetSimulation(simulation);
        var original = simulation.Snapshot;
        var comparison = new List<(string Label, BitmapSource Bitmap)>();
        foreach (var (width, height) in new[] { (800, 600), (360, 540) })
        {
            byte[]? topDown = null;
            foreach (var mode in new[] { LivingArcadeViewMode.TopDown, LivingArcadeViewMode.Perspective })
            {
                var sceneLevel = Enumerable.Range(1, 3).First(number => LivingArcadePresentationPolicy.ModeFor(seed, number) == mode);
                host.SetBackgroundScene(seed, sceneLevel);
                Assert.AreEqual(mode, host.ViewMode);
                host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                var bitmap = Capture(host, width, height);
                var pixels = Pixels(bitmap);
                Assert.IsTrue(ColorCount(pixels) >= 18, "The actual WPF Host must render its prepared scenery.");
                var fieldHeight = Math.Min(width * .88, height);
                var projection = new LivingArcadeProjection(mode == LivingArcadeViewMode.Perspective,
                    (width - fieldHeight) / 2, fieldHeight, fieldHeight);
                var expected = projection.Project(original.PlayerX, original.PlayerY);
                expected.Y += (height - fieldHeight) / 2;
                var hero = ColorBounds(bitmap, 0x9A, 0xD8, 0xDF,
                    new Rect(Math.Max(0, expected.X - 60), Math.Max(0, expected.Y - 60),
                        Math.Min(width, expected.X + 60) - Math.Max(0, expected.X - 60),
                        Math.Min(height, expected.Y + 60) - Math.Max(0, expected.Y - 60)));
                Assert.IsTrue(!hero.IsEmpty && hero.Width > 5, "The hero must remain visible at its projected position.");
                Assert.IsTrue(Math.Abs(hero.X + hero.Width / 2 - expected.X) < 9, "Presentation must not move the hero sideways.");
                if (mode == LivingArcadeViewMode.TopDown) topDown = pixels;
                else Assert.IsTrue(Difference(topDown!, pixels) > .15, "Changing presentation must visibly change the road and depth.");
                Save(bitmap, $"actual-host-{mode.ToString().ToLowerInvariant()}-{width}.png");
                if (width == 800) comparison.Add((mode == LivingArcadeViewMode.TopDown ? "2D · вид сверху" : "Псевдо-3D · перспектива", bitmap));
            }
        }
        Save(ContactSheet(comparison, 2, 800, 600), "actual-host-2d-vs-perspective.png");
        Assert.AreEqual(original.PlayerX, simulation.Snapshot.PlayerX);
        Assert.AreEqual(original.PlayerY, simulation.Snapshot.PlayerY);
        Assert.AreEqual(original.Health, simulation.Snapshot.Health);
        Assert.AreEqual(original.Progress, simulation.Snapshot.Progress);
        Assert.AreEqual(original.GameLevel, simulation.Snapshot.GameLevel);
    });

    [TestMethod]
    public Task TargetsAndProjectilesShareOneProjectedPositionAndEnemiesGrowWithDepth() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var sceneLevel = Enumerable.Range(1, 3).First(level => LivingArcadePresentationPolicy.ModeFor(73, level) == LivingArcadeViewMode.Perspective);
        host.SetBackgroundScene(73, sceneLevel);
        var projection = new LivingArcadeProjection(true, 100, 600, 600);
        var widths = new List<double>();
        foreach (var depth in new[] { .15, .5, .85 })
        {
            var target = new LivingArcadeEntityView("test", .27, depth, .05, "#FF00FF", "circle");
            var shot = new LivingArcadeEntityView("hostile", .27, depth, .01, "#00FF00", "circle");
            var targetImage = Paint(host, "DrawEntity", dc => [dc, target, 100d, 600d, 600d, false]);
            var shotImage = Paint(host, "DrawProjectile", dc => [dc, shot, 100d, 600d, 600d]);
            var targetBounds = ColorBounds(targetImage, 255, 0, 255);
            var shotBounds = ColorBounds(shotImage, 0, 255, 0);
            Assert.IsFalse(targetBounds.IsEmpty); Assert.IsFalse(shotBounds.IsEmpty);
            var expected = projection.Project(.27, depth);
            var targetCenter = new Point(targetBounds.X + targetBounds.Width / 2, targetBounds.Y + targetBounds.Height / 2);
            var shotCenter = new Point(shotBounds.X + shotBounds.Width / 2, shotBounds.Y + shotBounds.Height / 2);
            Assert.IsTrue((targetCenter - expected).Length < 1.5, "The enemy must use the shared world projection.");
            Assert.IsTrue((shotCenter - targetCenter).Length < 1.5, "A hit position must align visually for the projectile and target.");
            widths.Add(targetBounds.Width);
            Save(targetImage, $"target-depth-{depth.ToString("0.00", CultureInfo.InvariantCulture)}.png");
        }
        Assert.IsTrue(widths[1] > widths[0] * 1.2 && widths[2] > widths[1] * 1.2,
            "Enemy bodies must grow with depth, not merely move down a trapezoid.");
    });

    [TestMethod]
    public Task RealSimulationProducesAReadablePerspectiveBattleFrame() => ScenarioNavigationTests.Sta(() =>
    {
        const int seed = 73;
        using var host = new LivingArcadeGameHost();
        host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        var level = LivingArcadeLevelPolicy.CreateFirst(seed);
        level.Waves = Enumerable.Range(0, 12).Select(index => new LivingArcadeWave
        {
            At = .5 + index * .7, EnemyId = index % 4 == 0 ? "guard" : index % 3 == 0 ? "weaver" : "scout",
            Count = 3, X = index % 2 == 0 ? .22 : .72, Spacing = .075, SpawnInterval = .18
        }).ToList();
        level.MiniEvents = [new() { At = 4, X = .35, Kind = "bonus", BonusKind = "shield", Tutorial = true }];
        LivingArcadeLevelPolicy.Validate(level);
        var simulation = new LivingArcadeSimulation(level, new());
        var sceneLevel = Enumerable.Range(1, 3).First(number => LivingArcadePresentationPolicy.ModeFor(seed, number) == LivingArcadeViewMode.Perspective);
        host.SetBackgroundScene(seed, sceneLevel); host.SetSimulation(simulation);
        simulation.SetPaused(false);
        for (var frame = 0; frame < 900; frame++)
        {
            simulation.Advance(1d / 60, default);
            if (simulation.Snapshot.Progress >= 8 && simulation.Snapshot.Enemies.Count >= 4 && simulation.Snapshot.Shots.Count >= 3) break;
        }
        var snapshot = simulation.Snapshot;
        Assert.IsFalse(snapshot.IsDefeated);
        Assert.IsTrue(snapshot.Enemies.Count >= 4 && snapshot.Shots.Count >= 3,
            "This frame must come from actual simulation movement, spawning and firing rather than a hand-drawn mock battle.");
        TestContext.WriteLine($"Battle frame: {snapshot.Progress:0.00}s, {snapshot.Enemies.Count} enemies and {snapshot.Shots.Count} projectiles.");
        foreach (var (width, height) in new[] { (800, 600), (360, 540) })
        {
            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
            Save(Capture(host, width, height), $"actual-host-perspective-battle-{width}.png");
        }
    });

    [TestMethod]
    public Task RotatingShieldKeepsItsVulnerableOpeningOnTheProjectedWorldDirection() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var sceneLevel = Enumerable.Range(1, 3).First(level => LivingArcadePresentationPolicy.ModeFor(73, level) == LivingArcadeViewMode.Perspective);
        host.SetBackgroundScene(73, sceneLevel);
        var projection = new LivingArcadeProjection(true, 100, 600, 600);
        foreach (var x in new[] { .08, .92 })
        {
            const double depth = .65, worldRadius = .06;
            var enemy = new LivingArcadeEntityView("shield-test", x, depth, worldRadius, "#FF00FF", "circle",
                ShieldMode: "rotating", WeakSpotAngleDegrees: 180, WeakSpotWidthDegrees: 20);
            var bitmap = Paint(host, "DrawEntity", dc => [dc, enemy, 100d, 600d, 600d, false]);
            Point RingPoint(double degrees)
            {
                var radians = degrees * Math.PI / 180;
                return projection.Project(x + Math.Sin(radians) * worldRadius * 1.18,
                    depth - Math.Cos(radians) * worldRadius * 1.18);
            }
            Assert.AreEqual(0, ShieldPixelsNear(bitmap, RingPoint(180)),
                "The actual vulnerable world direction must remain visibly open, including near either road edge.");
            Assert.IsTrue(ShieldPixelsNear(bitmap, RingPoint(160)) > 0 && ShieldPixelsNear(bitmap, RingPoint(200)) > 0,
                "Protected directions next to the opening must retain a visible projected shield.");
            Assert.IsTrue(ShieldPixelsNear(bitmap, RingPoint(0)) > 0, "The opposite side remains protected.");
            Save(bitmap, $"projected-shield-{(x < .5 ? "left" : "right")}.png");
        }
    });

    [TestMethod]
    public Task ElongatedProjectilePointsAlongItsActualProjectedHeading() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var sceneLevel = Enumerable.Range(1, 3).First(level => LivingArcadePresentationPolicy.ModeFor(73, level) == LivingArcadeViewMode.Perspective);
        host.SetBackgroundScene(73, sceneLevel);
        var projection = new LivingArcadeProjection(true, 100, 600, 600);
        foreach (var x in new[] { .08, .5, .92 })
        foreach (var heading in new[] { -.17, .17 })
        {
            var shot = new LivingArcadeEntityView("smg", x, .65, .02, "#00FF00", "circle", Hostile: false)
                { HeadingRadians = heading };
            var bitmap = Paint(host, "DrawProjectile", dc => [dc, shot, 100d, 600d, 600d]);
            var pixels = Pixels(bitmap); var points = new List<Point>();
            for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var px = 0; px < bitmap.PixelWidth; px++)
            {
                var index = (y * bitmap.PixelWidth + px) * 4;
                if (pixels[index] == 0 && pixels[index + 1] == 255 && pixels[index + 2] == 0)
                    points.Add(new(px, y));
            }
            Assert.IsTrue(points.Count >= 20, "A real elongated projectile body must be visible.");
            var centerX = points.Average(point => point.X); var centerY = points.Average(point => point.Y);
            var xx = points.Sum(point => (point.X - centerX) * (point.X - centerX));
            var yy = points.Sum(point => (point.Y - centerY) * (point.Y - centerY));
            var xy = points.Sum(point => (point.X - centerX) * (point.Y - centerY));
            var axis = .5 * Math.Atan2(2 * xy, xx - yy);
            var actual = new Vector(Math.Cos(axis), Math.Sin(axis));
            var angle = projection.AimAngle(x, .65, heading);
            var expected = new Vector(Math.Sin(angle), -Math.Cos(angle));
            Assert.IsTrue(Math.Abs(Vector.Multiply(actual, expected)) > .99,
                "The drawn projectile's long axis must follow its projected velocity, including near the road edge.");
        }
    });

    private static int ShieldPixelsNear(BitmapSource bitmap, Point point)
    {
        var pixels = Pixels(bitmap); var count = 0;
        for (var y = Math.Max(0, (int)point.Y - 3); y <= Math.Min(bitmap.PixelHeight - 1, point.Y + 3); y++)
        for (var x = Math.Max(0, (int)point.X - 3); x <= Math.Min(bitmap.PixelWidth - 1, point.X + 3); x++)
        {
            var i = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[i] > 170 && pixels[i + 1] > 170 && pixels[i + 2] is > 50 and < 180) count++;
        }
        return count;
    }

    private static RenderTargetBitmap Paint(LivingArcadeGameHost host, string method, Func<DrawingContext, object[]> args)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            typeof(LivingArcadeGameHost).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args(dc));
        return Capture(visual, 800, 600);
    }

    private static RenderTargetBitmap Render(LivingArcadePerspectiveBackground renderer, double progress)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) renderer.Draw(dc, 600, 500, 100, 400, progress);
        return Capture(visual, 600, 500);
    }

    private static RenderTargetBitmap RenderTerrain(LivingArcadePerspectiveBackground renderer, double progress)
    {
        var visual = new DrawingVisual();
        var projection = new LivingArcadeProjection(true, 100, 400, 500);
        var terrain = typeof(LivingArcadePerspectiveBackground).GetMethod("DrawTerrain", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var dc = visual.RenderOpen()) terrain.Invoke(renderer, [dc, projection, 600d, 500d, progress]);
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

    private static Rect ColorBounds(BitmapSource bitmap, byte red, byte green, byte blue, Rect? region = null)
    {
        var pixels = Pixels(bitmap); var result = Rect.Empty;
        var search = region ?? new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        for (var y = (int)search.Top; y < Math.Min(bitmap.PixelHeight, search.Bottom); y++)
        for (var x = (int)search.Left; x < Math.Min(bitmap.PixelWidth, search.Right); x++)
        {
            var index = (y * bitmap.PixelWidth + x) * 4;
            if (pixels[index] == blue && pixels[index + 1] == green && pixels[index + 2] == red && pixels[index + 3] == 255)
                result.Union(new Point(x, y));
        }
        return result;
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

    private static void AssertReadableCorridor(BitmapSource bitmap, string kind)
    {
        var projection = new LivingArcadeProjection(true, 100, 400, 500);
        var pixels = Pixels(bitmap); var dark = 0; var count = 0;
        for (var depth = .2; depth <= .9; depth += .01)
        for (var x = .2; x < .8; x += .01)
        {
            var point = projection.Project(x, depth);
            var i = ((int)point.Y * bitmap.PixelWidth + (int)point.X) * 4;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) < 150) dark++;
            count++;
        }
        Assert.IsTrue(dark > count * .75, kind + " must keep the play corridor quieter than bright enemies/projectiles.");
    }

    private static RenderTargetBitmap ContactSheet(IReadOnlyList<(string Label, BitmapSource Bitmap)> frames, int columns, int tileWidth, int tileHeight)
    {
        var width = columns * tileWidth; var height = (int)Math.Ceiling(frames.Count / (double)columns) * (tileHeight + 40);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            for (var i = 0; i < frames.Count; i++)
            {
                var x = i % columns * tileWidth; var y = i / columns * (tileHeight + 40);
                dc.DrawImage(frames[i].Bitmap, new Rect(x, y + 40, tileWidth, tileHeight));
                dc.DrawText(new FormattedText(frames[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 23, Brushes.White, 1), new Point(x + 16, y + 5));
            }
        }
        return Capture(visual, width, height);
    }

    private void Save(BitmapSource bitmap, string name)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "_tmp", "arcade-perspective-20261107", "render"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(file)) encoder.Save(output);
        TestContext.AddResultFile(file);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string file = "") => file;
}
