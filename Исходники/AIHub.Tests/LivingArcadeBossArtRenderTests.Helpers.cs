using System.Globalization;
using System.IO;
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

public sealed partial class LivingArcadeBossArtRenderTests
{
    private static readonly string EvidenceDirectory = Path.Combine(FindWorkspace(), "_tmp", "arcade-boss-art-20261107", "render");

    private static LivingArcadeLevel QuietBossLevel(int seed) => new()
    {
        Number = 3, Seed = seed, Authored = true, DurationSeconds = 30, MiniEvents = [],
        EnemyTypes = [new() { Health = .25, Speed = .2, States = [new() { Motion = "line" }] }],
        Waves = [new() { At = 0, Count = 1, X = .9, SpawnInterval = 0 }]
    };

    private static LivingArcadeHeroState EarnedHero() => new()
    {
        PowerupBalanceVersion = LivingArcadeHeroBalance.Version,
        WeaponPower = 1.35, PermanentPower = 1.25, FireRate = 6, MaxHealth = 120, TotalKills = 70
    };

    private static void Silence(LivingArcadeGameHost host) { host.Audio.EffectsVolume = 0; host.Audio.MusicVolume = 0; }

    private static void AwaitBoss(LivingArcadeSimulation simulation)
    {
        for (var tick = 0; tick < 60 * 60 && simulation.Snapshot.BossPhase == 0
            && !simulation.Snapshot.IsComplete && !simulation.Snapshot.IsDefeated; tick++)
            simulation.Advance(1d / 60, default);
        Assert.AreEqual(1, simulation.Snapshot.BossPhase, "The real stage-end boss must have spawned.");
    }

    private static void FollowBoss(LivingArcadeSimulation simulation)
    {
        var state = simulation.Snapshot;
        var offset = (state.Enemies.SingleOrDefault(enemy => enemy.IsBoss)?.X ?? state.PlayerX) - state.PlayerX;
        simulation.Advance(1d / 60, new(Left: offset < -.015, Right: offset > .015));
    }

    private static List<(string Label, BitmapSource Bitmap)> FightAndCapture(LivingArcadeBossArt art,
        string directory, string prefix, TestContext? context)
    {
        LivingArcadeBossArtPolicy.Validate(art);
        var level = QuietBossLevel(123); level.BossArt = art;
        var simulation = new LivingArcadeSimulation(level, EarnedHero());
        using var host = new LivingArcadeGameHost(LivingArcadeLibrary.Load());
        Silence(host); host.SetSimulation(simulation); AwaitBoss(simulation);
        var captured = new HashSet<int>();
        var images = new List<(string Label, BitmapSource Bitmap)>();
        for (var tick = 0; tick < 90 * 60 && !simulation.Snapshot.IsComplete && !simulation.Snapshot.IsDefeated; tick++)
        {
            var state = simulation.Snapshot;
            var boss = state.Enemies.SingleOrDefault(enemy => enemy.IsBoss);
            if (boss is not null && boss.Flash <= 0 && !boss.Telegraph && captured.Add(state.BossPhase))
            {
                Assert.AreSame(art.Visual, boss.Visual, "The illustrated body must survive each simulation phase.");
                foreach (var view in new[] { LivingArcadeViewMode.TopDown, LivingArcadeViewMode.Perspective })
                {
                    SelectView(host, level.Seed, view);
                    var bitmap = Capture(host);
                    Assert.IsFalse(Field<bool>(host, "_renderFailed"));
                    Assert.IsTrue(DistinctColors(BossCrop(bitmap, state, view)) > 12, "A complete compound boss must be visible.");
                    Save(bitmap, $"{prefix}-phase-{state.BossPhase}-{view}.png", directory, context);
                    images.Add(($"Phase {state.BossPhase} · {view}", bitmap));
                }
            }
            FollowBoss(simulation);
        }
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, captured.ToArray());
        Assert.IsTrue(simulation.Snapshot.IsComplete, "Illustration must not change the real boss's killable three-phase battle.");
        Assert.IsFalse(simulation.Snapshot.IsDefeated);
        Assert.IsTrue(simulation.Snapshot.BossDefeated);
        return images;
    }

    internal static LivingArcadeBossArt DetailedArtwork()
    {
        var layersAsset = LivingArcadeLibrary.Load().Entries.First(entry => entry.Frames.Length > 0 && entry.Role != "character").Id;
        var visual = new LivingArcadeVisual { Mode = "vector", Animation = "cycle", AnimationAmount = 2,
            AnimationPeriod = 1, PixelArt = false,
            Layers = [new() { AssetId = layersAsset, X = 112, Y = 162, Width = 32, Height = 32, Animation = "pulse" }] };
        for (var frame = 0; frame < 3; frame++)
        {
            var shapes = new List<LivingArcadeVisualShape>
            {
                new() { Kind = "path", Data = "M64,40L192,40L212,204L44,204Z", Fill = "#234B68", Stroke = "#6BCDE8", StrokeWidth = 2 },
                Rect(16, 66, 38, 110, "#49647A"), Rect(202, 66, 38, 110, "#49647A"),
                Rect(70, 204, 38, 38, "#253546"), Rect(148, 204, 38, 38, "#253546"),
                Rect(76, 14, 104, 52, "#172738"), Rect(84, 25, 33, 13, frame == 1 ? "#FFA040" : "#40E6EC"),
                Rect(139, 25, 33, 13, frame == 2 ? "#FFA040" : "#40E6EC"),
                new() { Kind = "ellipse", X = 106, Y = 75, Width = 44, Height = 44, Fill = "#F79932", Stroke = "#F6DBA8", StrokeWidth = 3 }
            };
            for (var row = 0; row < 6; row++)
            for (var column = 0; column < 8; column++)
                shapes.Add(Rect(66 + column * 15, 122 + row * 11, 8, 5, ((row + column + frame) % 3) switch
                { 0 => "#2F93AC", 1 => "#7BBACA", _ => "#BDDDDC" }));
            for (var rail = 0; rail < 6; rail++)
                shapes.Add(new() { Kind = "line", X = 24, Y = 80 + rail * 14, Width = 22, Height = 3,
                    Fill = "none", Stroke = "#BCDBE4", StrokeWidth = 2 });
            visual.Frames.Add(new() { Seconds = .18 + frame * .04, Shapes = shapes });
        }
        return new() { TitleRu = "Проверочный многослойный страж", TitleEn = "Compound guardian fixture", Visual = visual, Source = "model" };
    }

    private static LivingArcadeVisualShape Rect(double x, double y, double width, double height, string color) =>
        new() { X = x, Y = y, Width = width, Height = height, Fill = color };

    private static void SelectView(LivingArcadeGameHost host, int seed, LivingArcadeViewMode view) => host.SetBackgroundScene(seed,
        Enumerable.Range(1, 3).First(number => LivingArcadePresentationPolicy.ModeFor(seed, number) == view));

    private static RenderTargetBitmap Capture(LivingArcadeGameHost host, int width = 800, int height = 600)
    {
        host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout(); host.InvalidateVisual();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host); return bitmap;
    }

    private static BitmapSource BossCrop(BitmapSource source, LivingArcadeSnapshot state, LivingArcadeViewMode view)
    {
        var boss = state.Enemies.Single(enemy => enemy.IsBoss);
        var height = Math.Min(source.PixelWidth * .88, source.PixelHeight);
        var top = (source.PixelHeight - height) / 2;
        var camera = new LivingArcadeProjection(view == LivingArcadeViewMode.Perspective,
            (source.PixelWidth - height) / 2, height, height);
        var point = camera.Project(boss.X, boss.Y);
        point.Y += top;
        var radius = boss.Radius * height * camera.ScaleAt(boss.Y);
        var bounds = new Rect(point.X - radius * 1.15, point.Y - radius * 1.15, radius * 2.3, radius * 2.3);
        bounds.Intersect(new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        return new CroppedBitmap(source, new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
    }

    private static byte[] Pixels(BitmapSource bitmap)
    { var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels; }

    private static int DistinctColors(BitmapSource bitmap)
    { var data = Pixels(bitmap); var colors = new HashSet<int>(); for (var index = 0; index < data.Length; index += 4) colors.Add(BitConverter.ToInt32(data, index)); return colors.Count; }

    private static double PixelDifference(BitmapSource before, BitmapSource after)
    {
        var first = Pixels(before); var second = Pixels(after); var changed = 0;
        for (var index = 0; index < first.Length; index += 4)
            if (Math.Abs(first[index] - second[index]) + Math.Abs(first[index + 1] - second[index + 1]) + Math.Abs(first[index + 2] - second[index + 2]) > 30) changed++;
        return changed / (first.Length / 4d);
    }

    private static BitmapSource ContactSheet(IReadOnlyList<(string Label, BitmapSource Bitmap)> images, int columns)
    {
        const int tileWidth = 320, tileHeight = 270;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, columns * tileWidth, ((images.Count + columns - 1) / columns) * tileHeight));
            for (var index = 0; index < images.Count; index++)
            {
                var x = index % columns * tileWidth; var y = index / columns * tileHeight;
                context.DrawText(new FormattedText(images[index].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 13, Brushes.White, 1), new Point(x + 8, y + 6));
                context.DrawImage(images[index].Bitmap, new Rect(x + 8, y + 30, tileWidth - 16, tileHeight - 38));
            }
        }
        var bitmap = new RenderTargetBitmap(columns * tileWidth, ((images.Count + columns - 1) / columns) * tileHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing); return bitmap;
    }

    private static void Save(BitmapSource bitmap, string name, string directory, TestContext? context)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path)) encoder.Save(output);
        context?.AddResultFile(path);
    }

    private static string FindWorkspace()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "VERSION"))) return folder.FullName;
        return Path.GetTempPath();
    }
}
