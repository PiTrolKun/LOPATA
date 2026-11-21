using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class LayerCanvas
{
    private const double FocusDurationMilliseconds = 250;
    private FocusAnimation? _focusAnimation;
    private double _focusZoomLimit;

    private sealed record FocusAnimation(long StartedAt, double StartZoom, Vector StartPan, double EndZoom, Vector EndPan);

    internal void FocusLayer()
    {
        if (ViewLocked || _source is null || _mask is not { Any: true } mask || ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = mask.Bounds(_source.Width);
        var scale = FitScale(bounds);
        var zoom = scale / BaseScale;
        var area = FocusArea;
        var pan = new Vector(area.Left - bounds.Left + (area.Width - bounds.Width) / 2.0,
            area.Top - bounds.Top + (area.Height - bounds.Height) / 2.0) * scale;

        StopLayerFocus();
        ClearGesture();
        if (zoom == _zoom && pan == _pan) return;
        // Tiny layers can fill the viewport, and the wheel can continue from that scale.
        _focusZoomLimit = Math.Max(_focusZoomLimit, zoom);
        _focusAnimation = new(Stopwatch.GetTimestamp(), _zoom, _pan, zoom, pan);
        CompositionTarget.Rendering += RenderLayerFocus;
    }

    private void RenderLayerFocus(object? sender, EventArgs e)
    {
        if (_focusAnimation is not { } transition) return;
        var elapsed = Stopwatch.GetElapsedTime(transition.StartedAt).TotalMilliseconds;
        if (elapsed >= FocusDurationMilliseconds)
        {
            _zoom = transition.EndZoom;
            _pan = transition.EndPan;
            StopLayerFocus();
        }
        else
        {
            var t = elapsed / FocusDurationMilliseconds;
            var progress = t * t * (3 - 2 * t);
            _zoom = transition.StartZoom + (transition.EndZoom - transition.StartZoom) * progress;
            _pan = transition.StartPan + (transition.EndPan - transition.StartPan) * progress;
        }
        UpdateViewport();
    }

    private void StopLayerFocus()
    {
        if (_focusAnimation is null) return;
        _focusAnimation = null;
        CompositionTarget.Rendering -= RenderLayerFocus;
    }
}
