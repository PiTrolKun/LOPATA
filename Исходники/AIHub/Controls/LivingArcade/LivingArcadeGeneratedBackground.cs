using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services.LivingArcade;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.LivingArcade;

/// <summary>Optional model-authored scenery. All input becomes bounded, frozen vector data before the frame loop.</summary>
public sealed partial class LivingArcadeGeneratedBackground
{
    private DrawingGroup? _horizon;
    private DrawingGroup[] _objects = [];
    private GroundLayer[] _groundLayers = [];
    private Brush _sky = Brushes.Black;
    private SolidColorBrush _ground = Brushes.Black, _road = Brushes.Black;
    private Pen _edge = new(Brushes.DimGray, 1);
    private const int PlacementCount = 18;
    private const double ObjectCycle = 1.45;

    public bool IsPrepared { get; private set; }
    public int CachedDrawingCount => IsPrepared ? _objects.Length + 1 : 0;
    public string BackgroundColor { get; private set; } = "#182733";

    public void Prepare(LivingArcadeGeneratedScenery scenery)
    {
        ArgumentNullException.ThrowIfNull(scenery);
        LivingArcadeSceneryArtPolicy.Validate(scenery);
        Clear();
        var sky = Muted(scenery.SkyColor, "#152333", 110);
        var horizon = Muted(scenery.HorizonColor, "#405567", 135);
        var gradient = new LinearGradientBrush(sky.Color, horizon.Color, new Point(0, 0), new Point(0, 1));
        gradient.Freeze(); _sky = gradient;
        _ground = Muted(scenery.GroundColor, "#182733", 110);
        _road = Muted(scenery.RoadColor, "#1C2D36", 85);
        BackgroundColor = $"#{_ground.Color.R:X2}{_ground.Color.G:X2}{_ground.Color.B:X2}";
        _edge = new(Muted(scenery.MarkColor, "#586A76", 130), 1); _edge.Freeze();
        _horizon = Compile(scenery.HorizonShapes, 256);
        var count = Math.Min(scenery.RoadsideObjects?.Count ?? 0, 6);
        _objects = new DrawingGroup[count];
        for (var i = 0; i < count; i++) _objects[i] = Compile(scenery.RoadsideObjects![i]?.Shapes, 128);
        PrepareGround(scenery.GroundShapes);
        IsPrepared = true;
    }

    public void Clear()
    {
        IsPrepared = false; _horizon = null; _objects = []; _groundLayers = [];
        _sky = _ground = _road = Brushes.Black; _edge = new(Brushes.DimGray, 1);
        BackgroundColor = "#182733";
    }

    public void Draw(DrawingContext dc, double width, double height, double left,
        double roadWidth, double progress, bool perspective)
    {
        if (!IsPrepared || !Positive(width) || !Positive(height) || !Positive(roadWidth) || !double.IsFinite(left)) return;
        progress = double.IsFinite(progress) ? progress : 0;
        var projection = new LivingArcadeProjection(perspective, left, roadWidth, height);
        var horizon = height * (perspective ? LivingArcadeProjection.HorizonFraction : 0);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        dc.DrawRectangle(_ground, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(_sky, null, new Rect(0, 0, width, height * .18));
        if (perspective) DrawHorizon(dc, width, horizon);
        dc.DrawRectangle(_ground, null, new Rect(0, horizon, width, height - horizon));
        var road = new StreamGeometry();
        using (var path = road.Open())
        {
            path.BeginFigure(projection.Project(0, 0), true, true);
            path.LineTo(projection.Project(1, 0), true, false);
            path.LineTo(projection.Project(1, 1.2), true, false);
            path.LineTo(projection.Project(0, 1.2), true, false);
        }
        dc.DrawGeometry(_road, null, road);
        DrawGround(dc, projection, progress);
        if (!perspective)
        {
            dc.PushOpacity(.35); DrawHorizon(dc, width, height * .18); dc.Pop();
        }
        dc.DrawLine(_edge, projection.Project(0, 0), projection.Project(0, 1.2));
        dc.DrawLine(_edge, projection.Project(1, 0), projection.Project(1, 1.2));
        if (_objects.Length > 0) DrawObjects(dc, projection, roadWidth, progress);
        dc.Pop();
    }

    private void DrawHorizon(DrawingContext dc, double width, double height)
    {
        dc.PushTransform(new ScaleTransform(width / 256, height / 256));
        dc.DrawDrawing(_horizon!); dc.Pop();
    }

    private void DrawObjects(DrawingContext dc, LivingArcadeProjection projection, double roadWidth, double progress)
    {
        var shift = Mod(progress * .095, ObjectCycle);
        var first = (int)Math.Ceiling((ObjectCycle - shift) * PlacementCount / ObjectCycle) % PlacementCount;
        for (var count = 0; count < PlacementCount; count++)
        {
            var index = (first + count) % PlacementCount;
            var y = Mod(index * ObjectCycle / PlacementCount + shift, ObjectCycle) - .18;
            if (y is < -.12 or > 1.18) continue;
            var margin = .28 + index * 17 % 11 * .035;
            var foot = projection.Project(index % 2 == 0 ? -margin : 1 + margin, y);
            var scale = projection.ScaleAt(y) * roadWidth * (.24 + index % 3 * .018) / 256;
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, foot.X - 128 * scale, foot.Y - 256 * scale));
            dc.DrawDrawing(_objects[index % _objects.Length]); dc.Pop();
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static double Mod(double value, double period) => (value % period + period) % period;
    private static Color ReadColor(string? value, string fallback)
    {
        var valid = value is { Length: 7 } && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        var hex = uint.Parse((valid ? value! : fallback).AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    }
    private static SolidColorBrush Muted(string? value, string fallback, double ceiling)
    {
        var color = ReadColor(value, fallback);
        var peak = Math.Max(color.R, Math.Max(color.G, color.B));
        var scale = peak > ceiling ? ceiling / peak : 1;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(color.R * scale), (byte)(color.G * scale), (byte)(color.B * scale)));
        brush.Freeze(); return brush;
    }
}
