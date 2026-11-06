using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGeneratedBackground
{
    private sealed record GroundContour(Point[] Points, bool Filled, bool Closed);
    private sealed record GroundLayer(SolidColorBrush Brush, Pen Pen, GroundContour[] Contours);

    private void PrepareGround(IReadOnlyList<LivingArcadeVisualShape>? shapes)
    {
        // Full detail remains in the horizon/objects. Ground detail uses a bounded simplified mesh,
        // so a valid but very intricate model drawing cannot multiply its cost every frame.
        var groups = new List<GroundContour>[12];
        for (var i = 0; i < groups.Length; i++) groups[i] = [];
        var remaining = 1024;
        for (var i = 0; i < Math.Min(shapes?.Count ?? 0, 128) && remaining >= 2; i++)
        {
            var shape = shapes![i];
            var geometry = ShapeGeometry(shape);
            if (geometry is null || shape is null || Alpha(shape.Opacity) < .05) continue;
            var filled = shape.Kind != "line" && shape.Fill != "none";
            var color = filled ? shape.Fill : shape.Stroke;
            if (color is null or "none") continue;
            var group = GroundColorGroup(ReadColor(color, "#607080"));
            var outline = geometry.GetFlattenedPathGeometry(3, ToleranceType.Absolute);
            foreach (var figure in outline.Figures)
            {
                var points = new List<Point>(32) { GroundPoint(figure.StartPoint) };
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line) points.Add(GroundPoint(line.Point));
                    else if (segment is PolyLineSegment polyline)
                        foreach (var point in polyline.Points) points.Add(GroundPoint(point));
                }
                if (points.Count < 2) continue;
                var count = Math.Min(64, Math.Min(remaining, points.Count));
                var compact = new Point[count];
                for (var p = 0; p < count; p++) compact[p] = points[p * (points.Count - 1) / (count - 1)];
                groups[group].Add(new(compact, filled && figure.IsFilled, figure.IsClosed));
                remaining -= count;
                if (remaining < 2) break;
            }
        }
        var result = new List<GroundLayer>(12);
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i].Count == 0) continue;
            var brush = GroundPaint(i); var pen = new Pen(brush, .7); pen.Freeze();
            result.Add(new(brush, pen, groups[i].ToArray()));
        }
        _groundLayers = result.ToArray();
    }

    private static Point GroundPoint(Point point) => new(Math.Clamp(point.X, 0, 256), Math.Clamp(point.Y, 0, 256));

    private void DrawGround(DrawingContext dc, LivingArcadeProjection projection, double progress)
    {
        const double tileDepth = .18;
        var shift = Mod(progress * .095, tileDepth);
        foreach (var layer in _groundLayers)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                for (var row = 0; row < 8; row++)
                {
                    var y = row * tileDepth + shift - tileDepth;
                    foreach (var contour in layer.Contours)
                    {
                        var point = contour.Points[0];
                        path.BeginFigure(projection.Project(-.3 + point.X / 160, y + point.Y / 256 * tileDepth),
                            contour.Filled, contour.Closed);
                        for (var p = 1; p < contour.Points.Length; p++)
                        {
                            point = contour.Points[p];
                            path.LineTo(projection.Project(-.3 + point.X / 160, y + point.Y / 256 * tileDepth), true, false);
                        }
                    }
                }
            }
            dc.DrawGeometry(layer.Brush, layer.Pen, geometry);
        }
    }

    private static int GroundColorGroup(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var hue = max - min < 25 ? 3 : max == color.R ? 0 : max == color.G ? 1 : 2;
        return hue * 3 + Math.Min(2, max / 86);
    }

    private static SolidColorBrush GroundPaint(int group)
    {
        var brightness = 45 + group % 3 * 27;
        var muted = brightness * .72;
        var hue = group / 3;
        var brush = new SolidColorBrush(Color.FromArgb(65,
            (byte)(hue is 0 or 3 ? brightness : muted),
            (byte)(hue is 1 or 3 ? brightness : muted),
            (byte)(hue is 2 or 3 ? brightness : muted)));
        brush.Freeze(); return brush;
    }
}
