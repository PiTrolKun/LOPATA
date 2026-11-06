using System.Windows;
using System.Windows.Media;
using AIHub.Models;
using Point = System.Windows.Point;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.LivingArcade;

public sealed partial class LivingArcadeGeneratedBackground
{
    private static DrawingGroup Compile(IReadOnlyList<LivingArcadeVisualShape>? shapes, int limit)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 256, 256)));
            for (var i = 0; i < Math.Min(shapes?.Count ?? 0, limit); i++)
            {
                var shape = shapes![i];
                var geometry = ShapeGeometry(shape);
                if (geometry is null || shape is null) continue;
                var fill = Paint(shape.Fill);
                var stroke = Paint(shape.Stroke);
                var pen = stroke is null || !Positive(shape.StrokeWidth) ? null : new Pen(stroke, Math.Min(16, shape.StrokeWidth));
                pen?.Freeze();
                dc.PushOpacity(Alpha(shape.Opacity));
                dc.DrawGeometry(shape.Kind == "line" ? null : fill, pen, geometry);
                dc.Pop();
            }
            dc.Pop();
        }
        drawing.Freeze(); return drawing;
    }

    private static SolidColorBrush? Paint(string? value)
    {
        if (value == "none" || string.IsNullOrWhiteSpace(value)) return null;
        var brush = new SolidColorBrush(ReadColor(value, "#8798A3")); brush.Freeze(); return brush;
    }

    private static double Alpha(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static Geometry? ShapeGeometry(LivingArcadeVisualShape? shape)
    {
        if (shape is null || !double.IsFinite(shape.X) || !double.IsFinite(shape.Y)
            || !double.IsFinite(shape.Width) || !double.IsFinite(shape.Height)) return null;
        Geometry? geometry = null;
        switch (shape.Kind)
        {
            case "rect" when Positive(shape.Width) && Positive(shape.Height):
                geometry = new RectangleGeometry(new Rect(Math.Clamp(shape.X, 0, 256), Math.Clamp(shape.Y, 0, 256),
                    Math.Min(256, shape.Width), Math.Min(256, shape.Height))); break;
            case "ellipse" when Positive(shape.Width) && Positive(shape.Height):
                geometry = new EllipseGeometry(new Rect(Math.Clamp(shape.X, 0, 256), Math.Clamp(shape.Y, 0, 256),
                    Math.Min(256, shape.Width), Math.Min(256, shape.Height))); break;
            case "line":
                geometry = new LineGeometry(new Point(Math.Clamp(shape.X, 0, 256), Math.Clamp(shape.Y, 0, 256)),
                    new Point(Math.Clamp(shape.X + shape.Width, 0, 256), Math.Clamp(shape.Y + shape.Height, 0, 256))); break;
            case "path" when shape.Data is { Length: > 0 and <= 4096 }:
                try
                {
                    // WPF geometry markup only: no SVG document, script, external image or URL is interpreted.
                    geometry = Geometry.Parse(shape.Data);
                    var bounds = geometry.Bounds;
                    if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
                        || bounds.Left < 0 || bounds.Top < 0 || bounds.Right > 256 || bounds.Bottom > 256) return null;
                }
                catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
                { return null; }
                break;
        }
        geometry?.Freeze(); return geometry;
    }
}
