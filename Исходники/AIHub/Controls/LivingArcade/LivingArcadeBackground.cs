using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

/// <summary>Bounded, cached top-down scenery. It never owns collision or simulation state.</summary>
public sealed partial class LivingArcadeBackground
{
    private const double Road = 640, Side = 192, Tile = 512;
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Color, double Width), Pen> _pens = new();
    private DrawingGroup? _road, _left, _right;
    private Palette _palette = Palettes[0];
    private int _seed, _variant;

    public string SceneKind { get; private set; } = "harbor";
    public string BackgroundColor => _palette.Ground;
    public bool IsPrepared => _road is not null;
    public int CachedDrawingCount => IsPrepared ? 3 : 0;

    public void Prepare(string sceneKind, int seed, int variant)
    {
        var kind = Array.IndexOf(Kinds, sceneKind);
        if (kind < 0) kind = 0;
        if (IsPrepared && SceneKind == Kinds[kind] && _seed == seed && _variant == variant) return;
        Clear();
        SceneKind = Kinds[kind]; _seed = seed; _variant = variant; _palette = Palettes[kind];
        _road = BuildTile(Road, dc => DrawRoad(dc, new Random(Mix(seed, variant, 17))));
        _left = BuildTile(Side, dc => DrawSide(dc, new Random(Mix(seed, variant, 43)), false));
        _right = BuildTile(Side, dc => DrawSide(dc, new Random(Mix(seed, variant, 79)), true));
    }

    public void Clear()
    {
        _road = _left = _right = null;
        _brushes.Clear(); _pens.Clear();
    }

    public void Draw(DrawingContext dc, double width, double height, double left, double roadWidth, double progress)
    {
        if (!IsPrepared || !FinitePositive(width) || !FinitePositive(height) || !FinitePositive(roadWidth)
            || !double.IsFinite(left)) return;
        // One world metric and constant-size repeated tiles: resizing cannot change gameplay coordinates.
        var scale = roadWidth / Road;
        var shift = ((double.IsFinite(progress) ? progress : 0) * 76) % Tile;
        if (shift < 0) shift += Tile;
        dc.DrawRectangle(B(_palette.Ground), null, new Rect(0, 0, width, height));
        DrawStrip(dc, _road!, left, roadWidth, height, scale, shift, Road);
        if (left > 0) DrawStrip(dc, _left!, 0, left, height, scale, shift, Side);
        var rightWidth = Math.Max(0, width - left - roadWidth);
        if (rightWidth > 0) DrawStrip(dc, _right!, left + roadWidth, rightWidth, height, scale, shift, Side);
        dc.DrawRectangle(B(_palette.Edge), null, new Rect(left, 0, Math.Max(1, 3 * scale), height));
        dc.DrawRectangle(B(_palette.Edge), null, new Rect(left + roadWidth - Math.Max(1, 3 * scale), 0,
            Math.Max(1, 3 * scale), height));
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;
    private static int Mix(int seed, int variant, int salt) => unchecked(seed * 397 ^ variant * 7919 ^ salt * 104729);
    private static void DrawStrip(DrawingContext dc, DrawingGroup tile, double x, double width,
        double height, double scale, double shift, double sourceWidth)
    {
        dc.PushClip(new RectangleGeometry(new Rect(x, 0, width, height)));
        dc.PushTransform(new TranslateTransform(x, 0));
        dc.PushTransform(new ScaleTransform(width / sourceWidth, scale));
        // At normal game aspect ratios this is four copies. The bound also protects diagnostics/malformed sizes.
        var rows = Math.Clamp((int)Math.Ceiling(height / (Tile * scale)) + 2, 2, 64);
        for (var row = -1; row < rows; row++)
        {
            dc.PushTransform(new TranslateTransform(0, row * Tile + shift));
            dc.DrawDrawing(tile);
            dc.Pop();
        }
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    private static DrawingGroup BuildTile(double width, Action<DrawingContext> draw)
    {
        var result = new DrawingGroup();
        using (var dc = result.Open())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, Tile)));
            draw(dc); dc.Pop();
        }
        result.Freeze();
        return result;
    }

    private SolidColorBrush B(string color)
    {
        if (_brushes.TryGetValue(color, out var cached)) return cached;
        var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        value.Freeze(); _brushes.Add(color, value); return value;
    }
    private Pen P(string color, double width = 1)
    {
        if (_pens.TryGetValue((color, width), out var cached)) return cached;
        var value = new Pen(B(color), width); value.Freeze(); _pens.Add((color, width), value); return value;
    }
    private void Rect(DrawingContext dc, string color, double x, double y, double w, double h, string? stroke = null)
    { if (w > 0 && h > 0) dc.DrawRectangle(B(color), stroke is null ? null : P(stroke), new Rect(x, y, w, h)); }
    private void Ellipse(DrawingContext dc, string color, double x, double y, double rx, double ry, string? stroke = null)
    { dc.DrawEllipse(B(color), stroke is null ? null : P(stroke), new Point(x, y), rx, ry); }
    private void Line(DrawingContext dc, string color, double x, double y, double endX, double endY, double width = 1)
    { dc.DrawLine(P(color, width), new Point(x, y), new Point(endX, endY)); }
    private void Shape(DrawingContext dc, string fill, Point[] points, string? stroke = null)
    {
        if (points.Length < 3) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        { context.BeginFigure(points[0], true, true); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        geometry.Freeze(); dc.DrawGeometry(B(fill), stroke is null ? null : P(stroke), geometry);
    }

    private sealed record Palette(string Ground, string Floor, string Seam, string Mark,
        string Edge, string Dark, string Mid, string Light, string Accent, string AccentDim);
    private static readonly string[] Kinds = ["harbor", "forest", "desert", "ice", "city", "alien"];
    private static readonly Palette[] Palettes =
    [
        new("#122532", "#1A2935", "#243642", "#354653", "#506D75", "#10212D", "#304A54", "#64848A", "#D39F50", "#65583D"),
        new("#142820", "#1B2C26", "#263A2F", "#34473A", "#4A6650", "#10241B", "#284F36", "#527B4B", "#B6AF74", "#55684A"),
        new("#443A2E", "#342F29", "#423A30", "#504536", "#8D7651", "#332D26", "#6A5238", "#A48558", "#CBA775", "#62513C"),
        new("#213943", "#1A2A38", "#283E4D", "#35505D", "#8CA7AB", "#182F3B", "#45626C", "#A8BFC3", "#96DADD", "#3E6878"),
        new("#171D35", "#182331", "#263243", "#2D4052", "#4B7381", "#111A2B", "#2B3D54", "#61718B", "#DC83BC", "#624565"),
        new("#232139", "#242935", "#303341", "#3A4350", "#69768B", "#1A2430", "#3B4A5B", "#6D7894", "#7AB8AD", "#3B6265")
    ];
}
