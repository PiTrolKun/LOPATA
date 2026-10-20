using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace AIHub.Controls;

public static class LyricLibraryIcon
{
    public static FrameworkElement CreateExamples()
    {
        var path = new ShapePath { Width = 24, Height = 24, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M14,21 H3 V2 H18 V9 M6,6 H14 M6,10 H11 M6,14 H9 M20,15 A5,5 0 1 1 10,15 A5,5 0 1 1 20,15 M19,19 L23,23") };
        path.SetResourceReference(ShapePath.StrokeProperty, "TextPrimaryBrush");
        return path;
    }
    public static FrameworkElement Create()
    {
        var canvas = new Canvas { Width = 28, Height = 28 };
        foreach (var (x, y, scale) in new[] { (12d, 0d, 1d), (5d, 5d, .8), (19d, 6d, .8) })
        {
            var pin = new ShapePath { Data = Geometry.Parse("M3,0 C0,0 0,3 1.5,5 C2.5,7 0,10 0,13 C0,16 6,16 6,13 C6,10 3.5,7 4.5,5 C6,3 6,0 3,0 Z"),
                StrokeThickness = 1.2, RenderTransform = new ScaleTransform(scale, scale) };
            pin.SetResourceReference(ShapePath.StrokeProperty, "TextPrimaryBrush");
            Canvas.SetLeft(pin, x); Canvas.SetTop(pin, y); canvas.Children.Add(pin);
        }
        var ball = new Ellipse { Width = 12, Height = 12 };
        ball.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Canvas.SetLeft(ball, 7); Canvas.SetTop(ball, 15); canvas.Children.Add(ball);
        foreach (var (x, y) in new[] { (10d, 18d), (14d, 18d), (12d, 21d) })
        {
            var hole = new Ellipse { Width = 1.8, Height = 1.8 }; hole.SetResourceReference(Shape.FillProperty, "WindowBackgroundBrush");
            Canvas.SetLeft(hole, x); Canvas.SetTop(hole, y); canvas.Children.Add(hole);
        }
        return new Viewbox { Width = 26, Height = 26, Child = canvas };
    }
}
