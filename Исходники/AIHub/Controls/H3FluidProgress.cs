using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace AIHub.Controls;

/// <summary>Small CPU-drawn activity animation; level always represents reported work.</summary>
internal sealed class H3FluidProgress : FrameworkElement
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private double _phase, _value;
    private bool _active;
    internal double? GpuUtilization { get; set; }
    internal double Value { get => _value; set { _value = Math.Clamp(value, 0, 100); InvalidateVisual(); } }
    internal bool Active
    {
        get => _active;
        set { _active = value; if (value && IsLoaded && SystemParameters.ClientAreaAnimation) _timer.Start(); else _timer.Stop(); InvalidateVisual(); }
    }
    internal H3FluidProgress()
    {
        _timer.Tick += (_, _) => { _phase += .22; InvalidateVisual(); };
        Loaded += (_, _) => { if (Active && SystemParameters.ClientAreaAnimation) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        AutomationProperties.SetName(this, "0%");
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth; var height = ActualHeight;
        if (width < 8 || height < 10) return;
        var panel = TryFindResource("PanelBrush") as Brush ?? Brushes.DimGray;
        var text = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 7, 7));
        drawing.DrawRectangle(panel, null, new Rect(0, 0, width, height));
        var top = height * (1 - Value / 100);
        var amplitude = Active ? GpuUtilization is { } load ? 1 + Math.Clamp(load, 0, 100) / 22 : 2.5 : 0;
        var path = new StreamGeometry();
        using (var context = path.Open())
        {
            context.BeginFigure(new Point(0, height), true, true);
            context.LineTo(new Point(0, top), true, false);
            for (var i = 0; i <= 24; i++)
            {
                var x = width * i / 24;
                var y = top + Math.Sin(_phase + i * .23) * amplitude + Math.Sin(_phase * 1.3 - i * .4) * amplitude * .35;
                context.LineTo(new Point(x, Math.Clamp(y, 0, height)), true, false);
            }
            context.LineTo(new Point(width, height), true, false);
        }
        path.Freeze();
        drawing.DrawGeometry(new SolidColorBrush(Active ? Color.FromRgb(38, 154, 109) : Color.FromRgb(78, 128, 110)), null, path);
        var caption = Math.Floor(Value).ToString(CultureInfo.CurrentCulture) + "%";
        AutomationProperties.SetName(this, caption);
        var formatted = new FormattedText(caption, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(formatted, new Point(Math.Max(1, (width - formatted.Width) / 2), Math.Max(2, height - 26)));
        drawing.Pop();
    }
}
