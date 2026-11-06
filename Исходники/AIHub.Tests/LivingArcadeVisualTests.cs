using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeVisualTests
{
    [TestMethod]
    public void VisualPolicyRejectsExecutableAndUnboundedArt()
    {
        foreach (var visual in new LivingArcadeVisual[]
        {
            new() { Mode = "library", AssetId = "../outside.png" },
            new() { AnimationPeriod = double.NaN },
            new() { Mode = "vector", Frames = [new() { Shapes = [new() { Kind = "path", Data = "<svg><script>run()</script></svg>" }] }] },
            new() { Mode = "vector", Frames = [new() { Shapes = [new() { Kind = "path", Data = "M0,0 L900,900 Z" }] }] },
            new() { Mode = "vector", Frames = [new() { Shapes = [new() { Kind = "rect", Width = 500, Height = 20 }] }] },
            new() { Palette = Enumerable.Range(0, 9).Select(_ => new LivingArcadePaletteSwap()).ToList() },
            new() { Decorations = Enumerable.Range(0, 17).Select(_ => new LivingArcadeVisualShape()).ToList() },
            new() { Layers = Enumerable.Range(0, 9).Select(_ => new LivingArcadeVisualLayer { AssetId = "part" }).ToList() },
            new() { Layers = [new() { AssetId = "part", Width = -2 }] },
            new() { Layers = [new() { AssetId = "part", X = 240, Width = 60, Height = 10 }] },
            new() { Layers = [new() { AssetId = "part", Rotation = 40 }] },
            new() { Layers = [new() { AssetId = "part", Rotation = 30 }] },
            new() { Layers = [new() { AssetId = "../part", Width = 30, Height = 30 }] }
        }) Assert.ThrowsExactly<InvalidDataException>(() => LivingArcadeVisualPolicy.Validate(visual));
        LivingArcadeVisualPolicy.Validate(null); // Old levels do not need an art descriptor.
        LivingArcadeVisualPolicy.Validate(Tank());
    }

    [TestMethod]
    public Task PreparedCompoundArtKeepsFramesAndPaletteWithoutReparsing() => ScenarioNavigationTests.Sta(() =>
    {
        var visual = Tank(); visual.Animation = "cycle";
        visual.Palette = [new() { From = "#22AA55", To = "#FF00FF" }];
        using var renderer = new RendererLease();
        renderer.Value.Prepare([new() { Visual = visual }]);
        var first = Capture(renderer.Value, visual, .02);
        var second = Capture(renderer.Value, visual, .19);
        Assert.IsTrue(Count(first, 255, 0, 255) > 200, "Exact model palette swaps must reach the drawn compound body.");
        Assert.IsTrue(Count(second, 255, 0, 255) > 200);
        CollectionAssert.AreNotEqual(Pixels(first), Pixels(second), "Cycle frames must change the barrel pose.");
        // Drawing consumes the prepared immutable geometry, not mutable generation data.
        visual.Frames[0].Shapes[0].Data = "invalid XML";
        CollectionAssert.AreEqual(Pixels(first), Pixels(Capture(renderer.Value, visual, .02)));
        Assert.ThrowsExactly<InvalidDataException>(() => renderer.Value.Prepare([new() { Visual = visual }]));
        CollectionAssert.AreEqual(Pixels(first), Pixels(Capture(renderer.Value, visual, .02)), "A rejected replacement must not destroy the previous prepared scene.");
        Assert.AreEqual(1, renderer.Value.PreparedCount);
        renderer.Value.Clear(); Assert.AreEqual(0, renderer.Value.PreparedCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => Capture(renderer.Value, visual, .02));
    });

    [TestMethod]
    public Task PixelArtKeepsHardEdgesAtThePreparedCanvasSize() => ScenarioNavigationTests.Sta(() =>
    {
        var visual = Tank(); visual.PixelArt = true;
        var renderer = new LivingArcadeSpriteRenderer(); renderer.Prepare([new() { Visual = visual }]);
        var pixels = Pixels(Capture(renderer, visual, 0));
        var blended = Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] is > 0 and < 255);
        Assert.AreEqual(0, blended, "The pixel drawing must not acquire soft translucent contour pixels.");
    });

    [TestMethod]
    public Task CatalogPartsPropsAndTilesComposeOverTheOriginalBody() => ScenarioNavigationTests.Sta(() =>
    {
        var library = LivingArcadeLibrary.Load();
        var visual = Tank();
        var renderer = new LivingArcadeSpriteRenderer(library); renderer.Prepare([new() { Visual = visual }]);
        var body = Capture(renderer, visual, 0);
        var roles = new[] { "part", "prop", "tile" };
        visual.Layers = roles.Select((role, index) => new LivingArcadeVisualLayer
        {
            AssetId = library.Entries.First(e => e.Role == role && e.Frames.Length > 0).Id,
            X = 24 + index * 80, Y = 16, Width = 48, Height = 48, Rotation = index == 1 ? 15 : 0,
            Animation = index == 1 ? "sway" : "none"
        }).ToList();
        renderer.Prepare([new() { Visual = visual }]);
        var composite = Capture(renderer, visual, .1);
        CollectionAssert.AreNotEqual(Pixels(body), Pixels(composite), "Parts, props and tiles must actually be drawn above the base.");
        Assert.IsTrue(Count(composite, 0x22, 0xAA, 0x55) > 200, "Adding library layers must retain the compound base body.");
        var sourceOnly = library.Entries.First(e => e.Frames.Length == 0);
        var unsupported = Tank(); unsupported.Layers = [new() { AssetId = sourceOnly.Id }];
        Assert.ThrowsExactly<InvalidDataException>(() => renderer.Prepare([new() { Visual = unsupported }]));
        CollectionAssert.AreEqual(Pixels(composite), Pixels(Capture(renderer, visual, .1)), "Rejecting a source-only layer must preserve the prepared scene.");
        visual.Layers = [new() { AssetId = "bluebird-walk-cycle", X = 64, Y = 32, Width = 128, Height = 128, Animation = "cycle" }];
        renderer.Prepare([new() { Visual = visual }]);
        CollectionAssert.AreNotEqual(Pixels(Capture(renderer, visual, .02)), Pixels(Capture(renderer, visual, .29)), "A layer cycle must advance its own prepared poses.");
        renderer.Clear(); Assert.AreEqual(0, renderer.PreparedCount);
    });

    [TestMethod]
    public Task ActualLibraryAndOriginalArtShareTheGameSurface() => ScenarioNavigationTests.Sta(() =>
    {
        var library = LivingArcadeLibrary.Load();
        LivingArcadeVisual Library(string id) => new() { Mode = "library", AssetId = id,
            Animation = library.Find(id)!.Frames.Length > 1 ? "cycle" : "bob", AnimationAmount = 2 };
        var level = LivingArcadeLevelPolicy.CreateFirst(511);
        var pixel = new LivingArcadeVisual { Mode = "vector", PixelArt = true, Animation = "sway", Frames =
            [new() { Shapes = [new() { X = 44, Y = 64, Width = 168, Height = 128, Fill = "#AE6BEB" },
                new() { X = 28, Y = 96, Width = 32, Height = 80, Fill = "#AE6BEB" },
                new() { X = 196, Y = 96, Width = 32, Height = 80, Fill = "#AE6BEB" },
                new() { X = 60, Y = 48, Width = 32, Height = 32, Fill = "#E8CCFF" },
                new() { X = 164, Y = 48, Width = 32, Height = 32, Fill = "#E8CCFF" },
                new() { X = 68, Y = 112, Width = 40, Height = 40, Fill = "#FFFFFF" },
                new() { X = 148, Y = 112, Width = 40, Height = 40, Fill = "#FFFFFF" },
                new() { X = 84, Y = 128, Width = 24, Height = 24, Fill = "#152335" },
                new() { X = 148, Y = 128, Width = 24, Height = 24, Fill = "#152335" },
                new() { X = 68, Y = 192, Width = 40, Height = 32, Fill = "#AE6BEB" },
                new() { X = 148, Y = 192, Width = 40, Height = 32, Fill = "#AE6BEB" }] }] };
        level.EnemyTypes = [
            new() { Id = "legacy_circle", Shape = "circle", Color = "#F07178" },
            new() { Id = "bird", Visual = Library("bluebird-walk-cycle") },
            new() { Id = "robot", Visual = Library("cartoon-robot") },
            new() { Id = "vector", Visual = Tank() },
            new() { Id = "legacy_star", Shape = "star", Color = "#F07178" },
            new() { Id = "dragon", Visual = Library("research-gemma-07-dragon") },
            new() { Id = "bot", Visual = Library("glitch-npc-maintenance-bot") },
            new() { Id = "pixel", Visual = pixel }];
        for (var i = 0; i < level.EnemyTypes.Count; i++)
        { level.EnemyTypes[i].Speed = i < 4 ? .04 : .10; level.EnemyTypes[i].Radius = .065; level.EnemyTypes[i].Health = 200; }
        level.Waves = level.EnemyTypes.Select((enemy, index) => new LivingArcadeWave
        { At = 0, EnemyId = enemy.Id, Count = 1, X = .17 + index % 4 * .22 }).ToList();
        level.MiniEvents = [];
        var simulation = new LivingArcadeSimulation(level, new());
        for (var i = 0; i < 48; i++) simulation.Advance(.125, default);
        using var host = new LivingArcadeGameHost(library); host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        host.SetSimulation(simulation); host.Measure(new Size(900, 700)); host.Arrange(new Rect(0, 0, 900, 700)); host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
        Assert.IsTrue(Count(bitmap, 0xF0, 0x71, 0x78) > 200, "The original procedural shapes must coexist with the library.");
        Assert.IsTrue(Count(bitmap, 0x22, 0xAA, 0x55) > 100, "The direct compound vector test drawing must be visible.");
        Assert.IsTrue(Count(bitmap, 0xAE, 0x6B, 0xEB) > 100, "The independent pixel drawing must be visible.");
        var output = Environment.GetEnvironmentVariable("LOPATA_ARCADE_MERGED_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "living-arcade-merged-scene.png")); encoder.Save(stream);
        }
    });

    [TestMethod]
    public Task PackagedCycleAndProceduralShapesCoexistAfterFilesAreClosed() => ScenarioNavigationTests.Sta(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-arcade-art-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var one = Path.Combine(folder, "one.png"); var two = Path.Combine(folder, "two.png");
        try
        {
            WritePng(one, Colors.Red); WritePng(two, Colors.Lime);
            var catalog = new
            {
                schemaVersion = 1, version = "test-1", packs = new[] { new { id = "test", title = "test", author = "test", license = "CC0", sourceUrl = "https://example.invalid", noticePath = "notice.txt" } },
                entries = new[] { new { id = "test-cycle", packId = "test", title = "test", tags = new[] { "test" }, role = "character", source = "one.png", sourceSha256 = "test",
                    frames = new[] { new { path = "one.png", seconds = .1 }, new { path = "two.png", seconds = .1 } }, animation = "cycle" } }
            };
            File.WriteAllText(Path.Combine(folder, "catalog.json"), JsonSerializer.Serialize(catalog));
            var library = LivingArcadeLibrary.Load(folder);
            var art = new LivingArcadeVisual { Mode = "library", AssetId = "test-cycle", Animation = "cycle" };
            var renderer = new LivingArcadeSpriteRenderer(library); renderer.Prepare([new() { Visual = art }]);
            File.Delete(one); File.Delete(two); // Decoder must have closed files and detached all pixel data.
            Assert.IsTrue(Count(Capture(renderer, art, .03), 255, 0, 0) > 1000);
            Assert.IsTrue(Count(Capture(renderer, art, .13), 0, 255, 0) > 1000);

            WritePng(one, Colors.Red); WritePng(two, Colors.Lime);
            var level = LivingArcadeLevelPolicy.CreateFirst(28);
            level.EnemyTypes = [new() { Id = "legacy", Shape = "star", Color = "#F07178", Health = 200 },
                new() { Id = "compound", Health = 200, Visual = Tank() }, new() { Id = "library", Health = 200, Visual = art }];
            level.Waves = [new() { At = 0, EnemyId = "legacy", X = .2 }, new() { At = 0, EnemyId = "compound", X = .5 }, new() { At = 0, EnemyId = "library", X = .8 }];
            level.MiniEvents = [];
            var simulation = new LivingArcadeSimulation(level, new());
            for (var i = 0; i < 16; i++) simulation.Advance(.125, default);
            using var host = new LivingArcadeGameHost(library); host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
            host.SetSimulation(simulation); host.Measure(new Size(800, 600)); host.Arrange(new Rect(0, 0, 800, 600));
            host.UpdateLayout(); var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
            Assert.IsTrue(Count(bitmap, 0xF0, 0x71, 0x78) > 60, "Legacy procedural enemies must remain visible.");
            Assert.IsTrue(Count(bitmap, 0x22, 0xAA, 0x55) > 20, "Direct compound model art must appear in the same scene.");
            Assert.IsTrue(Count(bitmap, 255, 0, 0) + Count(bitmap, 0, 255, 0) > 30, "Library art must appear alongside both existing and direct model art.");
        }
        finally
        {
            foreach (var path in new[] { one, two, Path.Combine(folder, "catalog.json") }) if (File.Exists(path)) File.Delete(path);
            Directory.Delete(folder);
        }
    });

    private static LivingArcadeVisual Tank() => new()
    {
        Mode = "vector", Frames = new[] { 92d, 106d }.Select(y => new LivingArcadeVisualFrame { Seconds = .15, Shapes =
        [new() { Kind = "path", Data = "M24,82 L216,82 L232,186 L24,186 Z", Fill = "#173E2C" },
            new() { X = 38, Y = 95, Width = 177, Height = 75, Fill = "#22AA55" },
            new() { X = 113, Y = y, Width = 21, Height = 92, Fill = "#FFFF00" },
            new() { Kind = "ellipse", X = 71, Y = 103, Width = 115, Height = 69, Fill = "#22AA55" }] }).ToList()
    };
    private static RenderTargetBitmap Capture(LivingArcadeSpriteRenderer renderer, LivingArcadeVisual visual, double age)
    {
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        { dc.PushTransform(new TranslateTransform(128, 128)); renderer.DrawBody(dc, visual, age, 128, false); renderer.DrawLayers(dc, visual, age, 128, false); renderer.DrawDecoration(dc, visual, 128, false); dc.Pop(); }
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); return bitmap;
    }
    private static void WritePng(string path, Color color)
    {
        var image = new DrawingVisual();
        using (var dc = image.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(16, 16, 224, 224));
        var bitmap = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); bitmap.Render(image);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static byte[] Pixels(BitmapSource bitmap)
    { var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    private static int Count(BitmapSource bitmap, byte r, byte g, byte b)
    { var pixels = Pixels(bitmap); var count = 0; for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] == b && pixels[i + 1] == g && pixels[i + 2] == r && pixels[i + 3] == 255) count++; return count; }
    private sealed class RendererLease : IDisposable
    { internal LivingArcadeSpriteRenderer Value { get; } = new(); public void Dispose() => Value.Clear(); }
}
