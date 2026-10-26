using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.VideoEditor;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoTimeline
{
    private ScrollViewer? _viewport;
    internal (double SourceIn, double Span) WaveSourceRange(VideoClip clip)
    {
        var start = clip.SourceIn; var span = clip.Duration * clip.Speed;
        if (_dragging.Contains(new(clip.Id)) && !_stretching) {
            if (_mode == "left") { start += _delta * clip.Speed; span -= _delta * clip.Speed; }
            if (_mode == "right") span += _delta * clip.Speed;
        }
        return (start, span);
    }
    private void DrawWaveform(DrawingContext dc, VideoClip clip, double x, double width, double y, VideoWaveform wave)
    {
        var visible = _viewport is null ? new Rect(0, 0, ActualWidth, ActualHeight) : new Rect(_viewport.TranslatePoint(new(), this), new System.Windows.Size(_viewport.ViewportWidth, _viewport.ViewportHeight));
        if (y + Row < visible.Top || y > visible.Bottom) return;
        var left = Math.Max(x, visible.Left); var right = Math.Min(x + width, visible.Right); if (right <= left) return;
        var (sourceIn, span) = WaveSourceRange(clip); var mid = y + 42; var heights = new List<(double X, double H)>();
        for (var at = Math.Floor(left); at < right; at++) {
            var a = sourceIn + Math.Max(0, at - x) / width * span; var b = sourceIn + Math.Min(width, at + 1 - x) / width * span;
            heights.Add((at + .5, wave.Peak(a, b) * 18));
        }
        var geometry = new StreamGeometry(); using (var context = geometry.Open()) {
            context.BeginFigure(new(left, mid), true, true);
            foreach (var point in heights) context.LineTo(new(point.X, mid - point.H), true, false);
            for (var i = heights.Count - 1; i >= 0; i--) context.LineTo(new(heights[i].X, mid + heights[i].H), true, false);
        }
        geometry.Freeze(); dc.DrawGeometry(Brushes.Aquamarine, null, geometry); dc.DrawLine(new Pen(Brushes.LightSeaGreen, .6), new(left, mid), new(right, mid));
    }
}
