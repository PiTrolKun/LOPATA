using System.IO;
using SkiaSharp;

namespace AIHub.Services;

public sealed record MusicCoverLayout(string[] Lines, float FontSize, int StripHeight)
{
    public int ImageHeight => 1024 - StripHeight;
    public static MusicCoverLayout Create(string artist, string title)
    {
        var caption = string.Join(" — ", new[] { artist.Trim(), title.Trim() }.Where(s => s.Length > 0));
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        for (var size = 40; size >= 16; size -= 2)
        {
            using var font = new SKFont(typeface, size);
            var lines = Wrap(caption, font);
            if (lines.Length <= 4 || size == 16)
            {
                var strip = Math.Clamp((int)Math.Ceiling((lines.Length * size * 1.35 + 32) / 64) * 64, 128, 384);
                return new(lines, size, strip);
            }
        }
        throw new InvalidDataException("Music.Cover.Invalid");
    }
    private static string[] Wrap(string text, SKFont font)
    {
        var lines = new List<string>(); var line = "";
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (font.MeasureText(line + element) > 960 && line.Length > 0) { lines.Add(line.Trim()); line = ""; }
            line += element;
        }
        if (line.Trim().Length > 0) lines.Add(line.Trim());
        return lines.ToArray();
    }
    public static void Compose(string source, string destination, string artist, string title)
    {
        var layout = Create(artist, title);
        // .NET handles long/Unicode Windows paths; the native Skia filename reader may not.
        using var image = SKBitmap.Decode(File.ReadAllBytes(source)) ?? throw new InvalidDataException("Music.Cover.Invalid");
        if (image.Width != 1024 || image.Height != layout.ImageHeight) throw new InvalidDataException("Music.Cover.Invalid");
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024));
        surface.Canvas.Clear(new SKColor(15, 23, 40));
        surface.Canvas.DrawBitmap(image, 0, 0);
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, layout.FontSize);
        using var paint = new SKPaint { Color = new SKColor(244, 241, 230), IsAntialias = true };
        var lineHeight = layout.FontSize * 1.35f;
        var first = layout.ImageHeight + (layout.StripHeight - layout.Lines.Length * lineHeight) / 2 + layout.FontSize;
        for (var i = 0; i < layout.Lines.Length; i++) surface.Canvas.DrawText(layout.Lines[i], 512, first + i * lineHeight, SKTextAlign.Center, font, paint);
        using var result = surface.Snapshot(); using var jpeg = result.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        jpeg.SaveTo(file); file.Flush(true);
    }
}
