using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

/// <summary>Cached roadside billboards and projected ground; presentation only, without a second simulation.</summary>
public sealed partial class LivingArcadePerspectiveBackground
{
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Color, double Width), Pen> _pens = new();
    private readonly DrawingGroup[] _landmarks = new DrawingGroup[6];
    private readonly Landmark[] _placements = new Landmark[24];
    private DrawingGroup? _distance;
    private LinearGradientBrush? _haze;
    private Palette _palette = Palettes[0];
    private int _seed, _variant;
    private const double Cycle = 1.45;
    private readonly record struct Landmark(double X, double Phase, int Drawing, double Scale);

    public string SceneKind { get; private set; } = "harbor";
    public bool IsPrepared => _distance is not null;
    public int CachedDrawingCount => IsPrepared ? _landmarks.Length + 1 : 0;
    public string BackgroundColor => _palette.Ground;

    public void Prepare(string kind, int seed, int variant)
    {
        var index = Array.IndexOf(Kinds, kind);
        if (index < 0) index = 0;
        if (IsPrepared && SceneKind == Kinds[index] && _seed == seed && _variant == variant) return;
        Clear(); SceneKind = Kinds[index]; _seed = seed; _variant = variant; _palette = Palettes[index];
        var random = new Random(unchecked(seed * 397 ^ variant * 7919 ^ 0x2A671));
        _distance = BuildDistance(random);
        for (var i = 0; i < _landmarks.Length; i++) _landmarks[i] = BuildLandmark(i);
        for (var i = 0; i < _placements.Length; i++)
        {
            var right = i % 2 != 0;
            var lane = .22 + random.NextDouble() * .38;
            _placements[i] = new(right ? 1 + lane : -lane,
                i * Cycle / _placements.Length, i % _landmarks.Length, .65 + random.NextDouble() * .38);
        }
        var haze = (Color)ColorConverter.ConvertFromString(_palette.Horizon);
        _haze = new(new Color() { A = 135, R = haze.R, G = haze.G, B = haze.B },
            new Color() { A = 0, R = haze.R, G = haze.G, B = haze.B }, new Point(0, 0), new Point(0, 1));
        _haze.Freeze();
    }

    public void Clear()
    {
        _distance = null; _haze = null;
        Array.Clear(_landmarks); Array.Clear(_placements);
        _brushes.Clear(); _pens.Clear();
    }

    public void Draw(DrawingContext dc, double width, double height, double left, double roadWidth, double progress)
    {
        if (!IsPrepared || !Positive(width) || !Positive(height) || !Positive(roadWidth) || !double.IsFinite(left)) return;
        progress = double.IsFinite(progress) ? progress : 0;
        var projection = new LivingArcadeProjection(true, left, roadWidth, height);
        dc.DrawRectangle(B(_palette.Sky), null, new Rect(0, 0, width, height));
        dc.PushTransform(new ScaleTransform(width / 1024, height / 720));
        dc.DrawDrawing(_distance!); dc.Pop();
        var horizon = height * LivingArcadeProjection.HorizonFraction;
        dc.DrawRectangle(B(_palette.Ground), null, new Rect(0, horizon, width, height - horizon));
        DrawTerrain(dc, projection, width, height, progress);

        // Sorted phase order becomes a cyclic rotation after scrolling. No per-frame list or sort.
        var shift = PositiveMod(progress * .095, Cycle);
        var first = (int)Math.Ceiling((Cycle - shift) * _placements.Length / Cycle) % _placements.Length;
        for (var count = 0; count < _placements.Length; count++)
        {
            var item = _placements[(first + count) % _placements.Length];
            var y = PositiveMod(item.Phase + shift, Cycle) - .18;
            if (y is < -.12 or > 1.18) continue;
            var foot = projection.Project(item.X, y);
            var scale = projection.ScaleAt(y) * roadWidth / 640 * item.Scale;
            // Full prefab width is below 180px; the anchor stays at least .22 world units outside the road.
            dc.PushTransform(new TranslateTransform(foot.X, foot.Y));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(_landmarks[item.Drawing]); dc.Pop(); dc.Pop();
        }
        dc.DrawRectangle(_haze!, null, new Rect(0, height * .10, width, height * .24));
    }

    private DrawingGroup BuildDistance(Random random)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            Rect(dc, _palette.Sky, 0, 0, 1024, 160);
            dc.DrawEllipse(B(_palette.Horizon), null, new Point(785, 38), 24, 24);
            var ridge = new Point[19]; ridge[0] = new(0, 112); ridge[^1] = new(1024, 112);
            for (var i = 1; i < ridge.Length - 1; i++) ridge[i] = new(i * 1024d / 17, 37 + random.Next(15, 51));
            Shape(dc, _palette.Far, ridge);
            for (var i = 0; i < 17; i++)
            {
                var x = i * 68 - 12; var h = random.Next(14, 47);
                if (SceneKind is "city" or "harbor" or "ice")
                {
                    Rect(dc, _palette.Horizon, x, 108 - h, random.Next(19, 44), h);
                    if (SceneKind == "harbor")
                    {
                        Line(dc, _palette.Horizon, x + 15, 65, x + 15, 111, 3);
                        Line(dc, _palette.Horizon, x + 15, 65, x + 43, 73, 3);
                    }
                }
                else if (SceneKind == "forest")
                    Shape(dc, _palette.Horizon, [new(x, 113), new(x + 20, 62 - h / 3), new(x + 40, 113)]);
                else if (SceneKind == "alien")
                {
                    Line(dc, _palette.Horizon, x + 17, 109, x + 21, 72 - h / 3, 5);
                    dc.DrawEllipse(B(_palette.Horizon), null, new Point(x + 21, 71 - h / 3), 25, 11);
                }
            }
        }
        group.Freeze(); return group;
    }

    private static bool Positive(double number) => double.IsFinite(number) && number > 0;
    private static double PositiveMod(double value, double divisor) => (value % divisor + divisor) % divisor;
    private SolidColorBrush B(string color)
    {
        if (_brushes.TryGetValue(color, out var result)) return result;
        result = new((Color)ColorConverter.ConvertFromString(color)); result.Freeze(); _brushes.Add(color, result); return result;
    }
    private Pen P(string color, double width = 1)
    {
        if (_pens.TryGetValue((color, width), out var result)) return result;
        result = new(B(color), width); result.Freeze(); _pens.Add((color, width), result); return result;
    }
    private void Rect(DrawingContext dc, string color, double x, double y, double w, double h) =>
        dc.DrawRectangle(B(color), null, new Rect(x, y, w, h));
    private void Line(DrawingContext dc, string color, double x, double y, double ex, double ey, double width = 1) =>
        dc.DrawLine(P(color, width), new Point(x, y), new Point(ex, ey));
    private void Shape(DrawingContext dc, string color, Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        { path.BeginFigure(points[0], true, true); path.PolyLineTo(points.AsSpan(1).ToArray(), true, false); }
        geometry.Freeze(); dc.DrawGeometry(B(color), null, geometry);
    }

    private sealed record Palette(string Sky, string Horizon, string Far, string Ground, string Floor,
        string Seam, string Mark, string Dark, string Mid, string Light, string Accent);
    private static readonly string[] Kinds = ["harbor", "forest", "desert", "ice", "city", "alien"];
    private static readonly Palette[] Palettes =
    [
        new("#101F2A", "#304C59", "#1D3540", "#122832", "#1D2D38", "#30444D", "#526B74", "#10212D", "#3A5560", "#748F96", "#A58C54"),
        new("#182C26", "#3A5546", "#253C32", "#152D23", "#23372D", "#33493A", "#52634B", "#12271E", "#345C3F", "#749169", "#A59E6F"),
        new("#473E34", "#73634E", "#5C4A39", "#4B3F30", "#39342C", "#534B3B", "#6C624B", "#302B25", "#795C3D", "#AF8D5B", "#B09B6C"),
        new("#263C4A", "#536F7A", "#3B5566", "#3B5360", "#223540", "#344D5B", "#617F87", "#1E3544", "#557887", "#A1BBC0", "#76B3BC"),
        new("#111A2D", "#36435F", "#253149", "#192334", "#1B2A36", "#2B3D4A", "#4B6171", "#141F31", "#354A61", "#718299", "#AE729C"),
        new("#211D35", "#50445E", "#36334C", "#28283D", "#29323D", "#3A4350", "#536474", "#1C2936", "#465B67", "#83929E", "#72A99A")
    ];
}
