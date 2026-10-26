using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

// Runs while the captured pointer is stationary beyond an edge, too.
internal sealed class VideoSelectionScroll(ScrollViewer viewport, Func<Point> pointer, Action changed)
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _attached;
    internal void Start()
    {
        if (!_attached) { _timer.Tick += (_, _) => Step(); _attached = true; }
        _timer.Start();
    }
    internal void Stop() => _timer.Stop();
    internal bool Running => _timer.IsEnabled;
    internal void Step()
    {
        var at = pointer(); var x = viewport.HorizontalOffset; var y = viewport.VerticalOffset;
        viewport.ScrollToHorizontalOffset(Math.Clamp(x + Speed(at.X, viewport.ViewportWidth), 0, viewport.ScrollableWidth));
        viewport.ScrollToVerticalOffset(Math.Clamp(y + Speed(at.Y, viewport.ViewportHeight), 0, viewport.ScrollableHeight));
        viewport.UpdateLayout();
        if (Math.Abs(viewport.HorizontalOffset - x) > .01 || Math.Abs(viewport.VerticalOffset - y) > .01) changed();
    }
    private static double Speed(double at, double extent)
    {
        const double edge = 28;
        if (extent <= 0) return 0;
        if (at < edge) return -Math.Clamp((edge - at) / 3, 2, 32);
        if (at > extent - edge) return Math.Clamp((at - extent + edge) / 3, 2, 32);
        return 0;
    }
}
