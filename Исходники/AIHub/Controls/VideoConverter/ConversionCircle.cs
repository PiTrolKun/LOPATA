using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AIHub.Controls.VideoConverter;

internal sealed class ConversionCircle : Grid
{
    private readonly Path _value = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly RotateTransform _rotation = new(0, 27, 27);
    private readonly TextBlock _text = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };

    internal ConversionCircle()
    {
        Width = Height = 54;
        var track = new Ellipse { StrokeThickness = 4, Margin = new(4) };
        track.SetResourceReference(Shape.StrokeProperty, "LineBrush");
        _value.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _value.RenderTransform = _rotation;
        Children.Add(track);
        Children.Add(_value);
        Children.Add(_text);
    }

    internal void ShowProgress(bool active, double? value)
    {
        var waiting = active && !value.HasValue;
        if (waiting != _rotation.HasAnimatedProperties)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, waiting
                ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        var fraction = waiting ? .28 : Math.Clamp(value ?? 0, 0, 1);
        _text.Text = waiting ? "…" : active || value.HasValue ? $"{fraction:P0}" : "";
        var angle = Math.PI * (Math.Clamp(fraction, 0, .9999) * 2 - .5);
        if (fraction == 0) { _value.Data = null; return; }
        var end = new Point(27 + 23 * Math.Cos(angle), 27 + 23 * Math.Sin(angle));
        _value.Data = new PathGeometry([new PathFigure(new Point(27, 4),
            [new ArcSegment(end, new Size(23, 23), 0, fraction > .5, SweepDirection.Clockwise, true)], false)]);
    }
}
