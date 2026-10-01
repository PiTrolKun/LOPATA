using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace AIHub.Controls;

internal static class ScenarioNavigationIcon
{
    public static FrameworkElement Create(string name)
    {
        var geometry = name switch
        {
            "create" => "M4,17 L16,5 Q17,4 18,5 L20,7 Q21,8 20,9 L8,21 L3,22 Z M14,7 L18,11 M4,17 L8,21 M3,22 L8,21",
            "analyze" => "M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M14.5,14.5 L21,21 M7,10 L9,12 L13,8",
            "experiment" => "M9,3 L15,3 M10,3 L10,10 L4,19 Q3,22 6,22 L18,22 Q21,22 20,19 L14,10 L14,3 M7,15 L17,15 M9,18 L9.1,18 M14,19 L14.1,19",
            "finance" => "M4,6 L18,3 L18,7 M4,6 Q2,6 2,9 L2,19 Q2,21 4,21 L20,21 Q22,21 22,19 L22,9 Q22,7 20,7 L4,7 M22,12 L16,12 L16,17 L22,17 M18,14.5 L18.1,14.5",
            _ => "M5,3 L5,8 L8,10 L11,8 L11,3 M5,3 Q1,8 6,12 L6,21 L10,21 L10,12 Q15,8 11,3 M17,3 L20,3 L20,7 L19,9 L19,14 M16,14 L22,14 L22,21 L16,21 Z"
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new Path
        {
            Data = Geometry.Parse(geometry), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        path.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = 58, Height = 58, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    }
}
