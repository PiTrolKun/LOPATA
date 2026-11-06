using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.LivingArcade;

/// <summary>Loads only selected art before play. Draw performs no parsing, decoding, or filesystem access.</summary>
internal sealed class LivingArcadeSpriteRenderer
{
    private sealed record Frame(ImageSource Image, ImageSource Flash, double Seconds);
    private sealed record Layer(Frame[] Frames, Rect Bounds, double Rotation, double Opacity,
        string Animation, double Amount, double Period);
    private sealed record Prepared(Frame[] Frames, Frame? Decoration, Layer[] Layers);
    private readonly Dictionary<LivingArcadeVisual, Prepared> _prepared = new(ReferenceEqualityComparer.Instance);
    private LivingArcadeLibrary? _library;

    internal LivingArcadeSpriteRenderer(LivingArcadeLibrary? library = null) => _library = library;
    internal int PreparedCount => _prepared.Count;

    internal void Prepare(IEnumerable<LivingArcadeEnemyDefinition> enemies)
    {
        var next = new Dictionary<LivingArcadeVisual, Prepared>(ReferenceEqualityComparer.Instance);
        var images = new Dictionary<string, (BitmapSource Image, BitmapSource Flash)>(StringComparer.OrdinalIgnoreCase);
        long preparedPixels = 0;
        foreach (var visual in enemies.Select(e => e.Visual).Where(v => v is not null).Distinct())
        {
            LivingArcadeVisualPolicy.Validate(visual);
            var frames = new List<Frame>();
            if (visual!.Mode == "library")
                frames.AddRange(LoadLibraryFrames(visual.AssetId!, visual.Palette, true, images, ref preparedPixels));
            else if (visual.Mode == "vector")
                foreach (var frame in visual.Frames)
                    frames.Add(DrawFrame(frame.Shapes, visual.Palette, frame.Seconds, visual.PixelArt));
            var decoration = visual.Decorations.Count == 0 ? null : DrawFrame(visual.Decorations, visual.Palette, 1, visual.PixelArt);
            var layers = new List<Layer>();
            foreach (var layer in visual.Layers)
                layers.Add(new(LoadLibraryFrames(layer.AssetId, visual.Palette, false, images, ref preparedPixels),
                    new(layer.X, layer.Y, layer.Width, layer.Height), layer.Rotation, layer.Opacity,
                    layer.Animation, visual.AnimationAmount, visual.AnimationPeriod));
            next.Add(visual, new(frames.ToArray(), decoration, layers.ToArray()));
        }
        _prepared.Clear();
        foreach (var pair in next) _prepared.Add(pair.Key, pair.Value);
    }

    internal bool DrawBody(DrawingContext dc, LivingArcadeVisual? visual, double age, double radius, bool flash)
    {
        if (visual is null || visual.Mode == "procedural") return false;
        var prepared = Get(visual);
        DrawFrame(dc, Select(prepared.Frames, visual.Animation == "cycle" ? age : 0), radius, flash);
        return true;
    }

    internal void DrawDecoration(DrawingContext dc, LivingArcadeVisual? visual, double radius, bool flash)
    {
        if (visual is null) return;
        if (Get(visual).Decoration is { } decoration) DrawFrame(dc, decoration, radius, flash);
    }

    internal void DrawLayers(DrawingContext dc, LivingArcadeVisual? visual, double age, double radius, bool flash)
    {
        if (visual is null) return;
        var layers = Get(visual).Layers;
        if (layers.Length == 0) return;
        dc.PushTransform(new ScaleTransform(radius / 128, radius / 128));
        dc.PushTransform(new TranslateTransform(-128, -128));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, 256, 256)));
        foreach (var layer in layers)
        {
            dc.PushOpacity(layer.Opacity);
            dc.PushTransform(new TranslateTransform(layer.Bounds.X + layer.Bounds.Width / 2, layer.Bounds.Y + layer.Bounds.Height / 2));
            dc.PushTransform(new RotateTransform(layer.Rotation));
            dc.PushTransform(AnimationTransform(layer.Animation, layer.Amount, layer.Period, age,
                Math.Max(layer.Bounds.Width, layer.Bounds.Height) / 2));
            var frame = Select(layer.Frames, layer.Animation == "cycle" ? age : 0);
            dc.DrawImage(flash ? frame.Flash : frame.Image,
                new Rect(-layer.Bounds.Width / 2, -layer.Bounds.Height / 2, layer.Bounds.Width, layer.Bounds.Height));
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    internal static Transform AnimationTransform(LivingArcadeVisual? visual, double age, double radius)
    {
        if (visual is null) return Transform.Identity;
        return AnimationTransform(visual.Animation, visual.AnimationAmount, visual.AnimationPeriod, age, radius);
    }

    private static Transform AnimationTransform(string animation, double amount, double period, double age, double radius)
    {
        if (animation is "none" or "cycle") return Transform.Identity;
        var phase = Math.Sin(age * Math.Tau / period);
        return animation switch
        {
            "bob" => new TranslateTransform(0, phase * radius * amount / 100),
            "sway" => new RotateTransform(phase * amount),
            "pulse" => new ScaleTransform(1 + phase * amount / 100, 1 + phase * amount / 100),
            _ => Transform.Identity
        };
    }

    private Frame[] LoadLibraryFrames(string id, IReadOnlyList<LivingArcadePaletteSwap> palette, bool character,
        Dictionary<string, (BitmapSource Image, BitmapSource Flash)> images, ref long preparedPixels)
    {
        _library ??= LivingArcadeLibrary.Load();
        var entry = _library.Find(id) ?? throw new InvalidDataException($"В библиотеке игры нет рисунка «{id}».");
        if (character && entry.Role != "character")
            throw new InvalidDataException("Этот элемент библиотеки не является готовым игровым персонажем.");
        if (entry.Frames.Length == 0) throw new InvalidDataException("У выбранного исходника библиотеки нет подготовленных игровых кадров.");
        var frames = new List<Frame>();
        var paletteKey = string.Join(';', palette.Select(p => p.From + p.To));
        foreach (var source in entry.Frames)
        {
            var path = _library.ResolvePath(source.Path);
            var key = path + "|" + paletteKey;
            if (!images.TryGetValue(key, out var image))
            {
                var bitmap = LoadBitmap(path, palette);
                preparedPixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                // Normal + flash copies stay within 128 MiB even for many compound layer combinations.
                if (preparedPixels > 16_777_216) throw new InvalidDataException("Слишком много кадров в составных рисунках этого уровня.");
                image = new(bitmap, White(bitmap)); images.Add(key, image);
            }
            frames.Add(new(image.Image, image.Flash, source.Seconds));
        }
        return frames.ToArray();
    }

    private Prepared Get(LivingArcadeVisual visual) => _prepared.TryGetValue(visual, out var prepared)
        ? prepared : throw new InvalidOperationException("Рисунок не был подготовлен перед запуском игры.");
    private static Frame Select(Frame[] frames, double age)
    {
        var total = frames.Sum(f => f.Seconds);
        var remaining = Math.Max(0, age) % total;
        foreach (var frame in frames)
        { if (remaining < frame.Seconds) return frame; remaining -= frame.Seconds; }
        return frames[^1];
    }
    private static void DrawFrame(DrawingContext dc, Frame frame, double radius, bool flash) =>
        dc.DrawImage(flash ? frame.Flash : frame.Image, new Rect(-radius, -radius, radius * 2, radius * 2));

    private static Frame DrawFrame(IReadOnlyList<LivingArcadeVisualShape> shapes,
        IReadOnlyList<LivingArcadePaletteSwap> palette, double seconds, bool pixelArt) =>
        new(DrawVector(shapes, palette, false, pixelArt), DrawVector(shapes, palette, true, pixelArt), seconds);

    private static DrawingImage DrawVector(IReadOnlyList<LivingArcadeVisualShape> shapes,
        IReadOnlyList<LivingArcadePaletteSwap> palette, bool white, bool pixelArt)
    {
        var drawing = new DrawingGroup();
        if (pixelArt) RenderOptions.SetEdgeMode(drawing, EdgeMode.Aliased);
        using (var dc = drawing.Open())
        {
            // A transparent canvas retains a common anchor across animation frames with different silhouettes.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 256, 256)));
            foreach (var shape in shapes)
            {
                var fill = Paint(shape.Fill, palette, white);
                var stroke = Paint(shape.Stroke, palette, white);
                var pen = stroke is null || shape.StrokeWidth == 0 ? null : new Pen(stroke, shape.StrokeWidth);
                pen?.Freeze();
                dc.PushOpacity(shape.Opacity);
                switch (shape.Kind)
                {
                    case "rect": dc.DrawRectangle(fill, pen, new(shape.X, shape.Y, shape.Width, shape.Height)); break;
                    case "ellipse": dc.DrawEllipse(fill, pen, new(shape.X + shape.Width / 2, shape.Y + shape.Height / 2), shape.Width / 2, shape.Height / 2); break;
                    case "line": if (pen is not null) dc.DrawLine(pen, new(shape.X, shape.Y), new(shape.X + shape.Width, shape.Y + shape.Height)); break;
                    case "path":
                        var geometry = Geometry.Parse(shape.Data!); geometry.Freeze();
                        dc.DrawGeometry(fill, pen, geometry); break;
                }
                dc.Pop();
            }
            dc.Pop();
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    private static SolidColorBrush? Paint(string color, IReadOnlyList<LivingArcadePaletteSwap> palette, bool white)
    {
        if (color == "none") return null;
        if (white) return Brushes.White;
        color = palette.FirstOrDefault(p => p.From.Equals(color, StringComparison.OrdinalIgnoreCase))?.To ?? color;
        var hex = uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var brush = new SolidColorBrush(Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));
        brush.Freeze(); return brush;
    }

    private static BitmapSource LoadBitmap(string path, IReadOnlyList<LivingArcadePaletteSwap> palette)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames[0];
        if (bitmap.PixelWidth is < 1 or > 512 || bitmap.PixelHeight is < 1 or > 512)
            throw new InvalidDataException("Размер рисунка библиотеки превышает разрешённый размер.");
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
        var replacements = palette.Select(p => (From: uint.Parse(p.From.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            To: uint.Parse(p.To.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture))).ToArray();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var color = (uint)(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
            foreach (var replacement in replacements)
                if (replacement.From == color)
                { pixels[i] = (byte)replacement.To; pixels[i + 1] = (byte)(replacement.To >> 8); pixels[i + 2] = (byte)(replacement.To >> 16); break; }
        }
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze(); return result;
    }
    private static BitmapSource White(BitmapSource source)
    {
        var stride = source.PixelWidth * 4; var pixels = new byte[stride * source.PixelHeight]; source.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4) pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze(); return result;
    }

    internal void Clear() => _prepared.Clear();
}
