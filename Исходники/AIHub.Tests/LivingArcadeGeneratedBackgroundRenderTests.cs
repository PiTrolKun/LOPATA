using System.Globalization;
using System.Reflection;
using System.Text.Json;
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
public sealed class LivingArcadeGeneratedBackgroundRenderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task SafeGeneratedArtworkRendersInBothViewsWithoutChangingTheSource() => ScenarioNavigationTests.Sta(() =>
    {
        var artwork = ComplexArtwork();
        LivingArcadeSceneryArtPolicy.Validate(artwork);
        var original = JsonSerializer.Serialize(artwork);
        var renderer = new LivingArcadeGeneratedBackground();
        renderer.Prepare(artwork);
        Assert.IsTrue(renderer.IsPrepared);
        Assert.IsTrue(renderer.CachedDrawingCount is > 0 and <= 8);
        var flat = Render(renderer, 17, false);
        var perspective = Render(renderer, 17, true);
        Assert.IsTrue(ColorCount(Pixels(flat)) >= 18 && ColorCount(Pixels(perspective)) >= 18,
            "The model's rectangles, ellipses, lines and curved paths must become detailed visible scenery.");
        Assert.IsTrue(Difference(Pixels(flat), Pixels(perspective)) > .12,
            "Generated artwork must adapt its ground and roadside objects to the chosen presentation.");
        CollectionAssert.AreEqual(Pixels(flat), Pixels(Render(renderer, 17, false)));
        CollectionAssert.AreEqual(Pixels(perspective), Pixels(Render(renderer, 17, true)));
        Assert.IsTrue(Difference(Pixels(perspective), Pixels(Render(renderer, 18.3, true))) > .005,
            "Generated scenery must approach the player rather than remain a static illustration.");
        Assert.AreEqual(original, JsonSerializer.Serialize(artwork), "Preparing geometry must not rewrite source model artwork.");
        Save(flat, "generated-artwork-topdown.png"); Save(perspective, "generated-artwork-perspective.png");
        Save(Compare(flat, perspective), "generated-artwork-both-views.png");
        renderer.Clear();
        Assert.IsFalse(renderer.IsPrepared); Assert.AreEqual(0, renderer.CachedDrawingCount);
        _ = Render(renderer, 17, true);
        renderer.Prepare(artwork);
        CollectionAssert.AreEqual(Pixels(perspective), Pixels(Render(renderer, 17, true)),
            "Returning to generated scenery must rebuild the same cached artwork.");
        renderer.Clear();
    });

    [TestMethod]
    public Task GeneratedCurvedGeometryKeepsItsRetainedCacheAndFrameCostBounded() => ScenarioNavigationTests.Sta(() =>
    {
        var renderer = new LivingArcadeGeneratedBackground();
        renderer.Prepare(ComplexArtwork());
        var cached = renderer.CachedDrawingCount;
        var visual = new DrawingVisual();
        void Draw(double progress)
        {
            using var dc = visual.RenderOpen();
            renderer.Draw(dc, 800, 600, 100, 600, progress, true);
        }
        for (var frame = 0; frame < 20; frame++) Draw(frame / 60d);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 200; frame++) Draw(10_000 + frame / 60d);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.WriteLine($"200 generated curved-art frames allocated {allocated:N0} managed bytes; drawings retained: {cached}.");
        Assert.IsTrue(allocated < 96L * 1024 * 1024,
            "Curved model art must be flattened/prepared once, with bounded projected ground work per frame.");
        Assert.AreEqual(cached, renderer.CachedDrawingCount);
        Assert.IsTrue(cached <= 8);
        renderer.Clear(); Draw(10_001);
        Assert.AreEqual(0, renderer.CachedDrawingCount);
    });

    [TestMethod]
    public Task ActualHostUsesGeneratedArtAndReleasesAllThreeSceneryCaches() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        var flat = Field<LivingArcadeBackground>(host, "_background");
        var perspective = Field<LivingArcadePerspectiveBackground>(host, "_perspectiveBackground");
        var generated = Field<LivingArcadeGeneratedBackground>(host, "_generatedBackground");
        var level = LivingArcadeLevelPolicy.CreateFirst(73);
        level.Waves = [new() { At = 100, EnemyId = "scout", Count = 1 }]; level.MiniEvents = [];
        var simulation = new LivingArcadeSimulation(level, new());
        host.SetSimulation(simulation);
        var sceneLevel = Enumerable.Range(1, 3).First(number => LivingArcadePresentationPolicy.ModeFor(73, number) == LivingArcadeViewMode.Perspective);
        host.SetBackgroundScene(73, sceneLevel);
        host.Measure(new Size(800, 600)); host.Arrange(new Rect(0, 0, 800, 600)); host.UpdateLayout();
        var builtin = Pixels(Capture(host, 800, 600));
        var artwork = ComplexArtwork();
        host.SetBackgroundScene(73, sceneLevel, artwork); host.UpdateLayout();
        Assert.IsTrue(host.HasGeneratedScenery);
        Assert.AreEqual(0, flat.CachedDrawingCount); Assert.AreEqual(0, perspective.CachedDrawingCount);
        Assert.IsTrue(generated.CachedDrawingCount is > 0 and <= 8);
        var bitmap = Capture(host, 800, 600);
        Assert.IsTrue(ColorCount(Pixels(bitmap)) >= 18 && Difference(builtin, Pixels(bitmap)) > .15,
            "The real host must actually select the model-authored place instead of displaying a prepared-but-unused cache.");
        Save(bitmap, "actual-host-generated-perspective.png");
        host.ReleaseGraphics();
        Assert.AreEqual(0, generated.CachedDrawingCount); Assert.IsFalse(host.HasGeneratedScenery);
        host.SetSimulation(simulation);
        Assert.IsTrue(host.HasGeneratedScenery, "Reopening a valid battle must restore its selected generated artwork.");
        host.SetBackgroundScene(73, sceneLevel);
        Assert.IsFalse(host.HasGeneratedScenery); Assert.AreEqual(0, generated.CachedDrawingCount);
        Assert.IsTrue(perspective.IsPrepared);
        host.SetBackgroundScene(73, sceneLevel, artwork);
        host.Dispose();
        Assert.AreEqual(0, flat.CachedDrawingCount); Assert.AreEqual(0, perspective.CachedDrawingCount);
        Assert.AreEqual(0, generated.CachedDrawingCount);
    });

    private static T Field<T>(LivingArcadeGameHost host, string name) where T : class =>
        (T)typeof(LivingArcadeGameHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    internal static Task RenderGenuineSceneryAsync(LivingArcadeGeneratedScenery artwork, string outputDirectory) =>
        ScenarioNavigationTests.Sta(() =>
        {
            LivingArcadeSceneryArtPolicy.Validate(artwork);
            Directory.CreateDirectory(outputDirectory);
            var renderer = new LivingArcadeGeneratedBackground();
            try
            {
                renderer.Prepare(artwork);
                var flat = Render(renderer, 17, false);
                var perspective = Render(renderer, 17, true);
                WriteEvidence(flat, Path.Combine(outputDirectory, "genuine-model-scenery-topdown.png"));
                WriteEvidence(perspective, Path.Combine(outputDirectory, "genuine-model-scenery-perspective.png"));
                WriteEvidence(Compare(flat, perspective), Path.Combine(outputDirectory, "genuine-model-scenery-both-views.png"));
            }
            finally { renderer.Clear(); }
        });

    private static void WriteEvidence(BitmapSource bitmap, string file)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }

    private static LivingArcadeGeneratedScenery ComplexArtwork()
    {
        var art = new LivingArcadeGeneratedScenery
        {
            TitleRu = "Обсерватория в незнакомом городе", TitleEn = "Observatory in an unfamiliar city",
            SkyColor = "#152336", HorizonColor = "#43556F", GroundColor = "#1C2B3B",
            RoadColor = "#243443", MarkColor = "#74818E"
        };
        art.HorizonShapes.Add(new() { Kind = "path", Data = "M0,120 C40,75 64,110 90,65 S150,100 180,70 S220,110 256,80 L256,150 L0,150 Z", Fill = "#40546A" });
        art.HorizonShapes.Add(new() { Kind = "ellipse", X = 209, Y = 15, Width = 25, Height = 25, Fill = "#81909E" });
        var colors = new[] { "#3B5063", "#4B6572", "#416373", "#667B84", "#768B91", "#91A4A1" };
        for (var building = 0; building < 12; building++)
        {
            var x = 4 + building * 20; var height = 35 + building * 17 % 61; var y = 133 - height;
            art.HorizonShapes.Add(new() { X = x, Y = y, Width = 15, Height = height, Fill = colors[building % colors.Length] });
            for (var floor = 0; floor < 4; floor++)
                art.HorizonShapes.Add(new() { X = x + 3, Y = y + 4 + floor * 6, Width = 3, Height = 3,
                    Fill = floor % 2 == 0 ? "#A7A893" : "#90A8A9" });
            art.HorizonShapes.Add(new() { Kind = "line", X = x + 7, Y = y, Width = 0, Height = -12,
                Fill = "none", Stroke = "#7C9098", StrokeWidth = 1.5 });
        }
        for (var tile = 0; tile < 12; tile++)
        {
            var x = 12 + tile % 4 * 60; var y = 12 + tile / 4 * 80;
            art.GroundShapes.Add(new() { X = x, Y = y, Width = 44, Height = 34, Fill = "#2D3D4B", Stroke = "#566775", StrokeWidth = .7 });
            art.GroundShapes.Add(new() { Kind = "ellipse", X = x + 10, Y = y + 7, Width = 22, Height = 17,
                Fill = "#3E535E", Stroke = "#687F86", StrokeWidth = .5 });
        }
        art.GroundShapes.Add(new() { Kind = "path", Data = "M8,240 C40,210 45,180 90,170 S175,155 240,30",
            Fill = "none", Stroke = "#6A7C84", StrokeWidth = 2 });
        art.RoadsideObjects =
        [
            new() { Name = "Observatory tower", Shapes =
            [
                new() { X = 88, Y = 60, Width = 80, Height = 172, Fill = "#46616B", Stroke = "#8DA4AA", StrokeWidth = 2 },
                new() { Kind = "ellipse", X = 67, Y = 25, Width = 122, Height = 70, Fill = "#70858E", Stroke = "#A9B8BC", StrokeWidth = 2 },
                new() { Kind = "path", Data = "M77,62 C102,40 144,42 181,61 L177,70 L80,70 Z", Fill = "#243E52" },
                new() { X = 106, Y = 112, Width = 44, Height = 66, Fill = "#1D3547", Stroke = "#9CB4B8", StrokeWidth = 2 },
                new() { Kind = "line", X = 128, Y = 18, Width = 0, Height = 65, Fill = "none", Stroke = "#C1C8BD", StrokeWidth = 3 }
            ] },
            new() { Name = "Luminous garden", Shapes =
            [
                new() { Kind = "path", Data = "M116,238 C123,177 77,122 125,37 C163,98 122,154 144,238 Z", Fill = "#526E64", Stroke = "#92A78E", StrokeWidth = 2 },
                new() { Kind = "ellipse", X = 32, Y = 86, Width = 100, Height = 48, Fill = "#688E8B", Stroke = "#A6B9A2", StrokeWidth = 2 },
                new() { Kind = "ellipse", X = 127, Y = 118, Width = 97, Height = 54, Fill = "#74779A", Stroke = "#B1A1B9", StrokeWidth = 2 },
                new() { Kind = "ellipse", X = 84, Y = 24, Width = 91, Height = 45, Fill = "#9BA195", Stroke = "#C9C8AF", StrokeWidth = 2 }
            ] },
            new() { Name = "Cable station", Shapes =
            [
                new() { X = 30, Y = 156, Width = 196, Height = 80, Fill = "#445766", Stroke = "#8B9DAB", StrokeWidth = 2 },
                new() { Kind = "path", Data = "M48,155 L74,67 L185,67 L208,155 Z", Fill = "#536B78", Stroke = "#90A5AE", StrokeWidth = 3 },
                new() { X = 82, Y = 91, Width = 97, Height = 42, Fill = "#162D40", Stroke = "#88A3AD", StrokeWidth = 2 },
                new() { Kind = "path", Data = "M11,48 Q126,105 245,48", Fill = "none", Stroke = "#A1AFB1", StrokeWidth = 3 },
                new() { Kind = "line", X = 126, Y = 74, Width = 0, Height = -30, Fill = "none", Stroke = "#A1AFB1", StrokeWidth = 3 }
            ] }
        ];
        return art;
    }

    private static RenderTargetBitmap Render(LivingArcadeGeneratedBackground renderer, double progress, bool perspective)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) renderer.Draw(dc, 800, 600, 100, 600, progress, perspective);
        return Capture(visual, 800, 600);
    }

    private static RenderTargetBitmap Capture(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }

    private static int ColorCount(byte[] pixels)
    {
        var colors = new HashSet<int>();
        for (var i = 0; i < pixels.Length; i += 16) colors.Add((pixels[i] / 8 << 10) | (pixels[i + 1] / 8 << 5) | pixels[i + 2] / 8);
        return colors.Count;
    }

    private static double Difference(byte[] first, byte[] second)
    {
        var changed = 0;
        for (var i = 0; i < first.Length; i += 4)
            if (Math.Abs(first[i] - second[i]) + Math.Abs(first[i + 1] - second[i + 1]) + Math.Abs(first[i + 2] - second[i + 2]) > 12) changed++;
        return changed / (first.Length / 4d);
    }

    private static RenderTargetBitmap Compare(BitmapSource flat, BitmapSource perspective)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 1600, 640));
            dc.DrawImage(flat, new Rect(0, 40, 800, 600)); dc.DrawImage(perspective, new Rect(800, 40, 800, 600));
            foreach (var (label, x) in new[] { ("Рисунок локации · 2D", 16), ("Тот же рисунок · псевдо-3D", 816) })
                dc.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 23, Brushes.White, 1), new Point(x, 5));
        }
        return Capture(visual, 1600, 640);
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
