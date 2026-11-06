using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;

namespace AIHub.Services.LivingArcade;

/// <summary>Fits safe decorative drawings to their canvas without relaxing combat-visual validation.</summary>
internal static class LivingArcadeSceneryGeometryNormalizer
{
    private const int MaximumPoints = 192;
    private static readonly Regex Number = new(@"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static void Fit(LivingArcadeGeneratedScenery art)
    {
        if (art.HorizonShapes is null || art.HorizonShapes.Count is < 1 or > 256
            || art.GroundShapes is null || art.GroundShapes.Count > 128
            || art.RoadsideObjects is null || art.RoadsideObjects.Count is < 1 or > 6
            || art.RoadsideObjects.Any(item => item is null || item.Shapes is null || item.Shapes.Count is < 1 or > 128)) throw Invalid();
        var canvases = new[] { art.HorizonShapes, art.GroundShapes }.Concat(art.RoadsideObjects.Select(item => item.Shapes)).ToArray();
        var all = canvases.SelectMany(canvas => canvas).ToArray();
        if (all.Any(shape => shape is null) || all.Sum(shape => (long)(shape.Data?.Length ?? 0)) > LivingArcadeSceneryArtPolicy.MaximumPathLength) throw Invalid();
        art.GeometryFitted = false; // The model cannot claim host-side normalization itself.
        foreach (var canvas in canvases) art.GeometryFitted |= FitCanvas(canvas);
    }

    // The boss artist reuses the finite affine/path fitter after applying its own canvas and readability limits.
    internal static bool FitCanvas(List<LivingArcadeVisualShape> shapes)
    {
        if (shapes.Count == 0) return false;
        var parsed = shapes.Select(Parse).ToArray();
        var bounds = Rect.Empty;
        foreach (var item in parsed) bounds.Union(item.Bounds);
        var outside = bounds.Left < 0 || bounds.Top < 0 || bounds.Right > 256 || bounds.Bottom > 256;
        var scale = outside ? Math.Min(1, Math.Min(bounds.Width > 0 ? 256 / bounds.Width : 1, bounds.Height > 0 ? 256 / bounds.Height : 1)) : 1;
        var tx = bounds.Left < 0 ? -bounds.Left * scale : Math.Min(0, 256 - bounds.Right * scale);
        var ty = bounds.Top < 0 ? -bounds.Top * scale : Math.Min(0, 256 - bounds.Bottom * scale);
        var changed = outside;
        Point Transform(Point point) => new(Math.Clamp(point.X * scale + tx, 0, 256), Math.Clamp(point.Y * scale + ty, 0, 256));
        foreach (var item in parsed)
        {
            var shape = item.Shape;
            if (item.Geometry is { } geometry)
            {
                changed |= shape.X != 0 || shape.Y != 0 || shape.Width != 0 || shape.Height != 0;
                shape.X = shape.Y = shape.Width = shape.Height = 0;
                if (outside) shape.Data = Flatten(geometry, Transform);
            }
            else if (outside)
            {
                var start = Transform(new(shape.X, shape.Y));
                var end = Transform(new(shape.X + shape.Width, shape.Y + shape.Height));
                shape.X = start.X; shape.Y = start.Y;
                shape.Width = end.X - start.X; shape.Height = end.Y - start.Y;
            }
            if (outside) shape.StrokeWidth *= scale;
        }
        return changed;
    }

    private sealed record Parsed(LivingArcadeVisualShape Shape, Rect Bounds, Geometry? Geometry);

    private static Parsed Parse(LivingArcadeVisualShape shape)
    {
        if (shape.Kind is not ("rect" or "ellipse" or "line" or "path")
            || !Finite(shape.X) || !Finite(shape.Y) || !Finite(shape.Width) || !Finite(shape.Height)
            || !double.IsFinite(shape.StrokeWidth) || shape.StrokeWidth is < 0 or > 16
            || !double.IsFinite(shape.Opacity) || shape.Opacity is < 0 or > 1) throw Invalid();
        if (shape.Kind != "path")
        {
            if (shape.Data is not null || !Finite(shape.X + shape.Width) || !Finite(shape.Y + shape.Height)
                || shape.Kind != "line" && (shape.Width <= 0 || shape.Height <= 0)) throw Invalid();
            return new(shape, new Rect(new Point(shape.X, shape.Y), new Point(shape.X + shape.Width, shape.Y + shape.Height)), null);
        }
        if (shape.Data is not { Length: > 0 and <= 4096 } data || data.Any(c => !PathCharacter(c))) throw Invalid();
        foreach (Match match in Number.Matches(data))
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !Finite(number)) throw Invalid();
        try
        {
            var geometry = Geometry.Parse(data);
            var bounds = geometry.Bounds;
            if (bounds.IsEmpty || !Finite(bounds.Left) || !Finite(bounds.Top) || !Finite(bounds.Right) || !Finite(bounds.Bottom)) throw Invalid();
            return new(shape, bounds, geometry);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
        { throw Invalid(); }
    }

    private sealed record Figure(List<Point> Points, bool Closed);

    private static string Flatten(Geometry geometry, Func<Point, Point> transform)
    {
        var figures = new List<Figure>();
        foreach (var figure in geometry.GetFlattenedPathGeometry(.5, ToleranceType.Absolute).Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment line) points.AddRange(line.Points);
                else if (segment is LineSegment single) points.Add(single.Point);
                else throw Invalid();
            figures.Add(new(points, figure.IsClosed));
        }
        if (figures.Count is < 1 or > 64) throw Invalid();
        var budgets = figures.Select(figure => Math.Min(figure.Points.Count, figure.Closed ? 3 : 2)).ToArray();
        var remaining = MaximumPoints - budgets.Sum();
        var extras = figures.Select((figure, index) => figure.Points.Count - budgets[index]).ToArray();
        var totalExtras = extras.Sum();
        if (totalExtras > remaining)
        {
            for (var index = 0; index < budgets.Length; index++)
            {
                var share = (int)((long)remaining * extras[index] / totalExtras);
                budgets[index] += share;
            }
        }
        else for (var index = 0; index < budgets.Length; index++) budgets[index] += extras[index];
        var text = new StringBuilder();
        for (var index = 0; index < figures.Count; index++)
        {
            var figure = figures[index]; var count = budgets[index];
            for (var point = 0; point < count; point++)
            {
                var selected = count == 1 ? 0 : (int)Math.Round((double)point * (figure.Points.Count - 1) / (count - 1));
                var position = transform(figure.Points[selected]);
                text.Append(point == 0 ? 'M' : 'L').Append(position.X.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append(',').Append(position.Y.ToString("0.###", CultureInfo.InvariantCulture));
            }
            if (figure.Closed) text.Append('Z');
        }
        if (text.Length is < 1 or > 4096) throw Invalid();
        return text.ToString();
    }

    private static bool Finite(double value) => double.IsFinite(value) && value is >= -4096 and <= 4096;
    private static bool PathCharacter(char value) => char.IsAsciiDigit(value) || char.IsWhiteSpace(value)
        || "MmLlHhVvCcSsQqTtAaZzEe.,+-".Contains(value);
    private static InvalidDataException Invalid() => new("Arcade.Ai.InvalidScenery");
}
