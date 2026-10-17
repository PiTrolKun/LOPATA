using System.Text.RegularExpressions;
using SkiaSharp;

namespace AIHub.Services.LyricVideo;

internal sealed class MatrixWeatherVisuals : IDisposable
{
    private readonly SceneDrawing _d;
    private readonly MusicWeather _weather;
    private readonly double _mainStart;
    private readonly SKTypeface _typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal);
    private readonly SKFont _font;
    private readonly SKPaint _paint = new() { IsAntialias = true };
    private readonly SKTextBlob[] _words;
    public int WordCount => _words.Length;

    public MatrixWeatherVisuals(ClipSession session, SceneDrawing drawing, MusicWeather weather, ClipOptions options)
    {
        _d = drawing; _weather = weather; _mainStart = session.MainStart; _font = new(_typeface, 18);
        _words = options.RainWords(session.SourceLyrics).Select(w => SKTextBlob.Create(w, _font)
            ?? throw new InvalidOperationException("Lyric rain allocation failed.")).ToArray();
    }
    public void Rain(SKCanvas c, double t, float opacity, bool reactive)
    {
        var w = reactive ? _weather.At(t) : new WeatherState(0, t * 42, t * .38);
        for (var layer = 0; layer < 3; layer++)
        {
            var columns = layer == 1 ? 51 : 43; var scale = .62f + layer * .34f;
            var layerSpeed = .60 + layer * .38; var spacing = 22 * scale;
            for (var col = 0; col < columns; col++)
            {
                var seed = Hash(col * 179 + layer * 1937 + 17);
                // Smooth density fades streams in instead of changing their identity.
                var presence = Math.Clamp((w.Density - seed % 1000 / 1000f) * 12, 0, 1);
                if (presence <= 0) continue;
                var period = 1400 + seed % 500; var travel = w.RainTravel * layerSpeed * (.7 + seed % 53 / 70d);
                var head = (float)((travel + seed % period) % period) - 90;
                var xBase = col * 1920f / columns + seed % 23 - 8;
                var cycle = (int)((travel + seed % period) / period);
                var trail = 4 + seed % 7 + (int)(w.Storm * 12);
                for (var row = 0; row < trail; row++)
                {
                    var y = head - row * spacing; if (y < -15 || y > 885) continue;
                    var x = xBase + w.Wind * (y - 420) + (float)Math.Sin(t * .8 + col * .4) * w.Storm * 14;
                    var textArea = x > 805 && y > 300 && y < 750;
                    var mask = textArea ? .16f : 1f;
                    var fade = 1 - row / (float)trail;
                    var alpha = (byte)Math.Clamp((52 + w.Storm * 178) * fade * opacity * presence * mask * (.6f + layer * .18f), 0, 245);
                    if (alpha < 2) continue;
                    var color = row == 0 ? new SKColor(158, 255, 213, alpha) : new SKColor(41, 227, 123, alpha);
                    if (w.Storm > .35f && row == 0)
                        _d.Line(c, x, y - 8 * scale, x - w.Wind * 24 * scale, y - 30 * scale, color.WithAlpha((byte)(alpha / 3)), scale);
                    c.Save(); c.Translate(x, y); c.Scale(scale);
                    var wordStream = _words.Length > 0 && seed % 5 == 0 && row == 1;
                    if (wordStream)
                    {
                        c.Scale(1.15f);
                        _paint.Color = color.WithAlpha((byte)(alpha * (textArea ? .45 : .82)));
                        c.DrawText(_words[Hash(seed + cycle * 47) % _words.Length], 0, 0, _paint);
                    }
                    else _d.Glyph(c, Hash(seed + row * 41 + (int)(t * (2 + layer * 2))), 0, 0, color);
                    c.Restore();
                }
            }
        }
    }
    public void Sea(SKCanvas c, double t)
    {
        var w = _weather.At(t);
        var fill = (float)Math.Clamp((t - _mainStart) / 4, 0, 1);
        _d.Glow(c, new(1110, 970), 760, new(17, 150, 84, (byte)(10 + w.Storm * 52)));
        for (var row = 0; row < 16; row++)
        {
            var depth = row / 15f; var half = 410 + depth * 610; var baseY = 847 + Math.Pow(depth, 1.5) * 219;
            for (var col = 0; col < 99; col++)
            {
                var x = 960 - half + col * half * 2 / 98;
                var phase = x * .007 + row * .54 - w.WaveTravel * 2.4;
                var wave = (Math.Sin(phase) + .43 * Math.Sin(x * .016 - row * .61 - w.WaveTravel * 3.1)) / 1.43;
                var y = (float)(baseY + wave * w.WaveHeight * (.25 + depth * .75));
                x += (float)Math.Sin(phase * 1.7) * w.Storm * 18;
                var crest = w.Storm > .30f && wave < -.45;
                var alpha = (byte)Math.Clamp((crest ? 90 + w.Storm * 140 : 17 + w.Storm * 93) * fill * (.30 + depth * .70), 0, 235);
                var color = crest ? new SKColor(145, 255, 209, alpha) : new SKColor(42, 212, 123, alpha);
                _d.Glyph(c, Hash(col * 31 + row * 71 + (int)(t * (1 + w.Storm * 5))), x, y, color);
                if (crest && col % 2 == 0) _d.Line(c, x - 7, y + 3, x + 9, y + 3, color.WithAlpha((byte)(alpha / 2)));
            }
        }
        Spray(c, t, fill);
    }
    private void Spray(SKCanvas c, double t, float fill)
    {
        for (var particle = 0; particle < 108; particle++)
        {
            var seed = Hash(particle * 211 + 717);
            var period = .75 + seed % 70 / 100d; var phase = seed % 1000 / 1000d * period;
            var birth = Math.Floor((t + phase) / period) * period - phase; var age = t - birth;
            var w = _weather.At(birth);
            if (w.Storm < .24 || birth < _mainStart) continue;
            var x = 65 + seed % 1790 + age * w.Wind * 180;
            var baseY = 890 + seed % 160;
            var y = baseY - (95 + w.Storm * 410) * age + 420 * age * age;
            var fade = (float)(1 - age / period);
            var alpha = (byte)Math.Clamp(w.Storm * 190 * fade * fill, 0, 210);
            _d.Glyph(c, seed + (int)birth, (float)x, (float)y, new(143, 255, 200, alpha));
        }
    }
    private static int Hash(int value)
    {
        unchecked { var x = (uint)value; x = (x ^ 61) ^ (x >> 16); x *= 9; x ^= x >> 4; x *= 0x27d4eb2d; x ^= x >> 15; return (int)(x & int.MaxValue); }
    }
    public void Dispose()
    {
        foreach (var word in _words) word.Dispose(); _paint.Dispose(); _font.Dispose(); _typeface.Dispose();
    }
}
