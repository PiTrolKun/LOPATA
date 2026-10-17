using System.Runtime.InteropServices;
using SkiaSharp;

namespace AIHub.Services.LyricVideo;

internal sealed class SceneRenderer : IDisposable
{
    public const int Width = 1920, Height = 1080, Fps = 30;
    private readonly ClipSession _session;
    private readonly ClipOptions _options;
    private readonly SKPaint? _palette;
    private readonly SceneDrawing _d = new();
    private readonly SKSurface _surface;
    private readonly SKPicture _monitor;
    private readonly SKBitmap? _cover;
    private readonly byte[] _pixels = new byte[Width * Height * 4];
    private readonly GCHandle _pinned;
    private readonly MusicWeather _weather;
    private readonly MatrixWeatherVisuals _visuals;
    private readonly SKPaint _imagePaint = new() { IsAntialias = true };
    private static readonly SKColor Green = new(108, 255, 177);
    private static readonly SKColor White = new(219, 244, 229);
    public SceneRenderer(ClipSession session, ClipOptions? options = null)
    {
        _session = session; _options = options ?? new(); _options.Validate();
        if (_options.Palette != "green") _palette = new() { ColorFilter = SKColorFilter.CreateColorMatrix(_options.Palette == "cyan"
            ? [.65f,0,0,0,0, 0,1,0,0,0, 0,.6f,.4f,0,0, 0,0,0,1,0]
            : [.5f,.5f,0,0,0, 0,.8f,0,0,0, 0,0,.35f,0,0, 0,0,0,1,0]) };
        _surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("Raster surface allocation failed.");
        _monitor = _d.Monitor(); _cover = session.Cover is null ? null : SKBitmap.Decode(session.Cover);
        _pinned = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        _weather = new MusicWeather(session.Analysis, _options.EffectScale); _visuals = new(session, _d, _weather, _options);
    }
    public WeatherState WeatherAt(double time) => _weather.At(time);
    public int LyricRainWordCount => _visuals.WordCount;
    public byte[] Render(double time)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        var c = _surface.Canvas;
        var tinted = _palette is not null && time >= _session.CoverDuration;
        if (tinted) c.SaveLayer(_palette);
        c.Clear(new SKColor(3, 10, 8));
        if (time < _session.CoverDuration && _cover is not null)
        {
            var scale = Math.Min(Width / (float)_cover.Width, Height / (float)_cover.Height);
            var w = _cover.Width * scale; var h = _cover.Height * scale;
            c.DrawBitmap(_cover, new SKRect((Width - w) / 2, (Height - h) / 2, (Width + w) / 2, (Height + h) / 2), _imagePaint);
        }
        else if (time < _session.CoverDuration + 3.5) Brand(c, time - _session.CoverDuration);
        else if (time < _session.MainStart) Title(c, time);
        else if (time >= _session.SongDuration) Credits(c, time);
        else Scene(c, time);
        if (tinted) c.Restore();
        if (!_surface.ReadPixels(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque), _pinned.AddrOfPinnedObject(), Width * 4, 0, 0))
            throw new InvalidOperationException("Raster readback failed.");
        return _pixels;
    }
    public byte[] Png(double time)
    {
        Render(time); using var image = _surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    private void Brand(SKCanvas c, double t)
    {
        var alpha = (byte)(255 * Math.Clamp(Math.Min(t / .35, (3.5 - t) / .35), 0, 1));
        _d.Text(c, "A PRODUCTION BY", 960, 443, 26, new SKColor(143, 170, 155, alpha), true);
        _d.Text(c, "LOPATA", 960, 548, 74, White.WithAlpha(alpha), true);
        _d.Line(c, 850, 600, 1070, 600, new SKColor(66, 173, 112, alpha), 2);
        _d.Text(c, "CREATED WITH LOCAL AI", 960, 669, 31, new SKColor(143, 170, 155, alpha), true);
    }
    private void Title(SKCanvas c, double t)
    {
        _visuals.Rain(c, t, .18f, false);
        _d.Glow(c, new(960, 580), 480, new(9, 68, 39, 65));
        _d.Fitted(c, _session.Artist, new(260, 330, 1660, 455), 42, new(133, 183, 151));
        _d.Fitted(c, _session.Title, new(220, 480, 1700, 720), 74, White);
        _d.Line(c, 820, 780, 1100, 780, Green.WithAlpha(120), 2);
    }
    private void Credits(SKCanvas c, double t)
    {
        _visuals.Rain(c, t, .12f, false);
        if (_session.Provenance.Models.Length == 0)
        { _d.Text(c, "LOPATA", 960, 540, 74, White, true); return; }
        _d.Text(c, _session.Localize("LyricVideo.Credits"), 960, 275, 28, new(126, 169, 141), true);
        var y = 398f;
        foreach (var model in _session.Provenance.Models)
        {
            _d.Text(c, _session.Localize("LyricVideo.Role." + model.Role).ToUpperInvariant(), 960, y, 24, new(116, 160, 131), true);
            _d.Fitted(c, model.Name, new(260, y + 8, 1660, y + 85), 43, White); y += 142;
        }
        _d.Text(c, "LOPATA", 960, 940, 32, Green, true);
    }
    private void Scene(SKCanvas c, double t)
    {
        var a = _session.Analysis.At(t); _visuals.Rain(c, t, 1, true);
        _d.Glow(c, new(480, 650), 490, new(16, 122, 61, (byte)(35 + a.Level * 23)));
        _d.Glow(c, new(1250, 880), 590, new(15, 75, 41, (byte)(20 + a.Bass * 35)));
        _visuals.Sea(c, t);
        // Broad projection cone behind text; motion remains tied to the song clock.
        _d.Path(c, [new(674, 451), new(1740, 303), new(1774, 736), new(673, 592)], new(40, 203, 114, 7));
        _d.Line(c, 674, 451, 1740, 303, new(72, 220, 146, 24));
        _d.Line(c, 673, 592, 1774, 736, new(72, 220, 146, 20));
        c.DrawPicture(_monitor); Screen(c, t, a);
        var line = _session.Lyrics?.FindLine(t);
        if (line is not null)
        {
            var text = _session.Lyrics!.SourceLyrics.Substring(line.SourceStart, line.SourceLength);
            var fade = (float)Math.Clamp(Math.Min((t - line.StartSeconds) / .15, (line.EndSeconds - t) / .15), 0, 1);
            var box = new SKRect(860, 345, 1810, 683);
            _d.Glow(c, new(1320, 520), 400, new(15, 75, 46, (byte)(fade * 35)));
            c.Save(); c.ClipRect(box);
            _d.Fitted(c, text, box, 57 * _options.TextScale, new(157, 255, 199, (byte)(fade * 255)));
            c.Restore();
            _d.Line(c, 1000, 720, 1668, 720, new(69, 214, 130, (byte)(45 * fade)), 1);
        }
    }
    private void Screen(SKCanvas c, double t, AudioEnergy a)
    {
        c.Save(); using var clip = new SKRoundRect(new SKRect(193, 357, 683, 656), 24, 24); c.ClipRoundRect(clip);
        for (var col = 0; col < 24; col++)
        for (var row = 0; row < 9; row++)
        {
            var y = 357 + (float)((row * 39 + t * (38 + col % 7 * 9)) % 325);
            _d.Glyph(c, Hash(col + row * 13 + (int)(t * 4)), 199 + col * 21, y, new(63, 231, 121, 75));
        }
        for (var i = 0; i < 30; i++)
        {
            var height = (float)(8 + (Math.Sin(i * .47 - t * 2) + 1) * (15 + a.Bass * 45) + a.High * 15);
            _d.Rect(c, new(211 + i * 15, 611 - height, 219 + i * 15, 611), new(85, 255, 142, 170), 2);
        }
        _d.Glow(c, new(438, 490), 260, new(15, 134, 68, 38));
        for (var y = 358; y < 656; y += 4) _d.Line(c, 190, y, 685, y, new(0, 0, 0, 54));
        _d.Gradient(c, new(195, 359, 681, 430), new(192, 255, 220, 24), new(0, 0, 0, 0), 23);
        c.Restore();
    }
    private static int Hash(int value)
    {
        unchecked { var x = (uint)value; x = (x ^ 61) ^ (x >> 16); x *= 9; x ^= x >> 4; x *= 0x27d4eb2d; x ^= x >> 15; return (int)(x & int.MaxValue); }
    }
    public void Dispose()
    {
        _palette?.ColorFilter?.Dispose(); _palette?.Dispose(); _visuals.Dispose(); _pinned.Free(); _cover?.Dispose(); _monitor.Dispose(); _surface.Dispose(); _imagePaint.Dispose(); _d.Dispose();
    }
}
