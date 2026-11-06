using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls.LivingArcade;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeRenderTests
{
    [TestMethod]
    public Task WpfSurfacePaintsActualGameplayAtWideAndCompactSizes() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost { BackgroundColor = "#0C1621" };
        var simulation = TestSimulation();
        for (var i = 0; i < 32; i++) simulation.Advance(.125, default);
        host.SetSimulation(simulation);
        var window = new Window { Content = new ContentControl { Content = host,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch },
            Width = 800, Height = 600, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        try
        {
            window.Show(); window.UpdateLayout();
            foreach (var (width, height) in new[] { (800d, 600d), (360d, 540d) })
            {
                window.Width = width; window.Height = height; window.UpdateLayout();
                Assert.IsTrue(host.ActualWidth >= width - 2 && host.ActualHeight >= height - 2,
                    "The drawing surface must occupy the content area, not collapse to zero desired size.");
                var bitmap = Capture(host);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Assert.AreEqual((byte)255, pixels[3], "The scenery must paint an opaque background.");
                Assert.IsTrue(CountColor(pixels, 0x9A, 0xD8, 0xDF) > 30, "Shovel hero must be visible.");
                Assert.IsTrue(CountColor(pixels, 0xF0, 0x71, 0x78) > 60, "Model-defined enemy shapes must be visible.");
                var enemyPixels = Enumerable.Range(0, bitmap.PixelHeight).SelectMany(y => Enumerable.Range(0, bitmap.PixelWidth)
                    .Where(x => IsColor(pixels, (y * bitmap.PixelWidth + x) * 4, 0xF0, 0x71, 0x78)).Select(x => new Point(x, y))).ToArray();
                var minEnemyX = enemyPixels.Min(p => p.X);
                var firstCircle = enemyPixels.Where(p => p.X < minEnemyX + 40).ToArray();
                var diameterX = firstCircle.Max(p => p.X) - firstCircle.Min(p => p.X);
                var diameterY = firstCircle.Max(p => p.Y) - firstCircle.Min(p => p.Y);
                Assert.IsTrue(Math.Abs(diameterX - diameterY) <= 2, "Normalized physics circles must stay physically circular at every viewport aspect.");
                var fieldHeight = Math.Min(width * .88, height);
                var fieldLeft = (width - fieldHeight) / 2;
                var fieldTop = (height - fieldHeight) / 2;
                var corridor = new Rect(fieldLeft + fieldHeight * .15, fieldTop + fieldHeight * .2,
                    fieldHeight * .7, fieldHeight * .5);
                var dark = 0; var samples = 0;
                for (var y = (int)corridor.Top; y < corridor.Bottom; y += 2)
                for (var x = (int)corridor.Left; x < corridor.Right; x += 2)
                {
                    var offset = (y * bitmap.PixelWidth + x) * 4;
                    if (Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])) < 150) dark++;
                    samples++;
                }
                Assert.IsTrue(dark > samples * .8,
                    "The combat corridor must remain muted behind visible enemies and bullets in every scenery.");
                var directory = Path.Combine(Path.GetTempPath(), "lopata-living-arcade-render");
                Directory.CreateDirectory(directory);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"living-arcade-{width:0}.png")); encoder.Save(output);
            }
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task RenderedBulletKeepsTenDegreeAimInBothViewportAspects() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        var window = new Window { Content = host, Left = -10000, Top = -10000, ShowActivated = false,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        try
        {
            window.Show();
            foreach (var (width, height) in new[] { (800d, 600d), (360d, 540d) })
            {
                window.Width = width; window.Height = height;
                var level = LivingArcadeLevelPolicy.CreateFirst(31);
                level.Waves = [new() { At = 20, EnemyId = "scout", Count = 1 }]; level.MiniEvents = [];
                var simulation = new LivingArcadeSimulation(level, new());
                simulation.Advance(1d / 60, new(Right: true));
                host.SetSimulation(simulation); window.UpdateLayout();
                Assert.AreEqual(1, simulation.Snapshot.Shots.Count, "The initial shot must exist before checking its rendered position.");
                var startBitmap = Capture(host); SaveFrame(startBitmap, $"angle-{width:0}-start.png");
                var start = BulletCenter(startBitmap);
                simulation.SetPaused(false); simulation.Advance(.15, new(Right: true));
                host.SetSimulation(simulation); window.UpdateLayout();
                Assert.AreEqual(1, simulation.Snapshot.Shots.Count, "Track one projectile rather than averaging several shots.");
                var endBitmap = Capture(host); SaveFrame(endBitmap, $"angle-{width:0}-end.png");
                var end = BulletCenter(endBitmap);
                var angle = Math.Atan2(end.X - start.X, start.Y - end.Y) * 180 / Math.PI;
                Assert.IsTrue(Math.Abs(angle - 10) < 2.5, $"Physical bullet angle was {angle:0.0} degrees for {width}×{height}.");
            }
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public Task HidingRunningSurfacePausesSimulationAndClearsInput() => ScenarioNavigationTests.Sta(() =>
    {
        using var host = new LivingArcadeGameHost();
        host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0;
        host.SetSimulation(TestSimulation());
        var window = new Window { Content = host, Width = 400, Height = 500,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            var pauses = 0; host.PauseRequested += () => pauses++;
            host.Start(); Assert.IsTrue(host.IsRunning);
            host.Visibility = Visibility.Collapsed;
            Assert.AreEqual(1, pauses);
            Assert.IsFalse(host.IsRunning);
            Assert.IsTrue(host.Snapshot!.IsPaused);
            Assert.AreEqual(default(LivingArcadeInput), host.ReadInput());
        }
        finally { window.Close(); }
    });

    private static LivingArcadeSimulation TestSimulation()
    {
        var level = LivingArcadeLevelPolicy.CreateFirst(17);
        string[] shapes = ["circle", "square", "diamond", "triangle", "star"];
        level.EnemyTypes = shapes.Select((shape, i) => new LivingArcadeEnemyDefinition
        { Id = "test" + i, Shape = shape, Color = "#F07178", Health = 200, Speed = .07 }).ToList();
        level.Waves = shapes.Select((_, i) => new LivingArcadeWave
        { At = 0, EnemyId = "test" + i, Count = 1, X = .12 + i * .18 }).ToList();
        level.MiniEvents = [];
        return new(level, new());
    }
    private static RenderTargetBitmap Capture(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); return bitmap;
    }
    private static void SaveFrame(RenderTargetBitmap bitmap, string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lopata-living-arcade-render"); Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
    private static Point BulletCenter(RenderTargetBitmap bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var points = Enumerable.Range(0, bitmap.PixelHeight).SelectMany(y => Enumerable.Range(0, bitmap.PixelWidth)
            .Where(x => IsColor(pixels, (y * bitmap.PixelWidth + x) * 4, 0xFD, 0xF0, 0x9E)).Select(x => new Point(x, y))).ToArray();
        Assert.IsTrue(points.Length > 0, "The actual rendered player bullet was not found.");
        return new(points.Average(p => p.X), points.Average(p => p.Y));
    }
    private static int CountColor(byte[] pixels, byte r, byte g, byte b)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (IsColor(pixels, i, r, g, b)) count++;
        return count;
    }
    private static bool IsColor(byte[] pixels, int index, byte r, byte g, byte b) =>
        pixels[index] == b && pixels[index + 1] == g && pixels[index + 2] == r && pixels[index + 3] == 255;
}
