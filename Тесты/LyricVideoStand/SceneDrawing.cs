using SkiaSharp;

namespace LyricVideoStand;

internal sealed class SceneDrawing : IDisposable
{
    public readonly SKTypeface Face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal);
    private readonly SKTypeface _mono = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal);
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKFont _font;
    private readonly SKFont _small;
    private readonly SKTextBlob[] _glyphs;
    public SceneDrawing()
    {
        _font = new(Face, 36); _small = new(_mono, 19);
        _glyphs = "01{}[]:;/*+-<>".Select(ch => SKTextBlob.Create(ch.ToString(), _small)
            ?? throw new InvalidOperationException("Glyph atlas allocation failed.")).ToArray();
    }
    public void Rect(SKCanvas c, SKRect rect, SKColor color, float radius = 0)
    {
        _paint.Shader = null; _paint.Style = SKPaintStyle.Fill; _paint.Color = color;
        c.DrawRoundRect(rect, radius, radius, _paint);
    }
    public void Line(SKCanvas c, float x, float y, float xx, float yy, SKColor color, float width = 1)
    {
        _paint.Shader = null; _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = width; _paint.Color = color;
        c.DrawLine(x, y, xx, yy, _paint);
    }
    public void Path(SKCanvas c, SKPoint[] points, SKColor color)
    {
        using var path = new SKPath(); path.AddPoly(points);
        _paint.Shader = null; _paint.Style = SKPaintStyle.Fill; _paint.Color = color; c.DrawPath(path, _paint);
    }
    public void Glow(SKCanvas c, SKPoint centre, float radius, SKColor color)
    {
        using var shader = SKShader.CreateRadialGradient(centre, radius, [color, color.WithAlpha(0)], null, SKShaderTileMode.Clamp);
        _paint.Style = SKPaintStyle.Fill; _paint.Color = SKColors.White; _paint.Shader = shader;
        c.DrawCircle(centre, radius, _paint); _paint.Shader = null;
    }
    public void Gradient(SKCanvas c, SKRect rect, SKColor a, SKColor b, float radius = 0)
    {
        using var shader = SKShader.CreateLinearGradient(new(rect.Left, rect.Top), new(rect.Right, rect.Bottom), [a, b], null, SKShaderTileMode.Clamp);
        _paint.Style = SKPaintStyle.Fill; _paint.Color = SKColors.White; _paint.Shader = shader;
        c.DrawRoundRect(rect, radius, radius, _paint); _paint.Shader = null;
    }
    public void Text(SKCanvas c, string text, float x, float y, float size, SKColor color, bool centred = false)
    {
        _font.Size = size; _paint.Style = SKPaintStyle.Fill; _paint.Color = color; _paint.Shader = null;
        c.DrawText(text, x, y, centred ? SKTextAlign.Center : SKTextAlign.Left, _font, _paint);
    }
    public void Glyph(SKCanvas c, int value, float x, float y, SKColor color)
    {
        _paint.Style = SKPaintStyle.Fill; _paint.Shader = null; _paint.Color = color;
        c.DrawText(_glyphs[(value & int.MaxValue) % _glyphs.Length], x, y, _paint);
    }
    public string[] Wrap(string text, float size, float width)
    {
        _font.Size = size; var result = new List<string>(); var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (_font.MeasureText(candidate) <= width) { current = candidate; continue; }
            if (current.Length > 0) result.Add(current);
            current = "";
            foreach (var ch in word)
            {
                if (_font.MeasureText(current + ch) > width && current.Length > 0) { result.Add(current); current = ""; }
                current += ch;
            }
        }
        if (current.Length > 0) result.Add(current);
        return result.ToArray();
    }
    public void Fitted(SKCanvas c, string text, SKRect area, float maximum, SKColor color)
    {
        var size = maximum; var lines = Wrap(text, size, area.Width);
        while (size > 20 && lines.Length * size * 1.35 > area.Height) { size -= 2; lines = Wrap(text, size, area.Width); }
        var y = area.MidY - (lines.Length - 1) * size * .675f + size * .32f;
        foreach (var line in lines) { Text(c, line, area.MidX, y, size, color, true); y += size * 1.35f; }
    }
    public SKPicture Monitor()
    {
        using var recorder = new SKPictureRecorder(); var c = recorder.BeginRecording(new(0, 0, 1920, 1080));
        Path(c, [new(726, 320), new(807, 365), new(795, 740), new(716, 750)], new(25, 41, 36));
        for (var i = 0; i < 13; i++) Line(c, 750, 440 + i * 14, 785, 454 + i * 14, new(4, 17, 12), 5);
        Gradient(c, new(143, 311, 737, 751), new(144, 160, 143), new(47, 67, 57), 32);
        Gradient(c, new(154, 322, 722, 740), new(201, 209, 178), new(79, 104, 83), 25);
        Rect(c, new(181, 347, 695, 669), new(12, 31, 23), 30);
        Rect(c, new(191, 356, 684, 656), new(1, 12, 7), 24);
        Line(c, 175, 684, 703, 684, new(45, 66, 52), 2);
        Glow(c, new(214, 708), 26, new(46, 239, 126, 110));
        Rect(c, new(204, 698, 223, 717), new(81, 255, 157), 10);
        Text(c, "LOPATA", 260, 716, 16, new(27, 55, 37));
        for (var i = 0; i < 4; i++) Rect(c, new(572 + i * 25, 699, 586 + i * 25, 713), new(36, 55, 43), 7);
        Path(c, [new(372, 750), new(537, 750), new(568, 811), new(340, 811)], new(47, 66, 50));
        Gradient(c, new(295, 798, 611, 830), new(110, 136, 109), new(37, 60, 43), 13);
        return recorder.EndRecording();
    }
    public void Dispose()
    {
        foreach (var glyph in _glyphs) glyph.Dispose(); _small.Dispose(); _font.Dispose();
        _paint.Dispose(); Face.Dispose(); _mono.Dispose();
    }
}
