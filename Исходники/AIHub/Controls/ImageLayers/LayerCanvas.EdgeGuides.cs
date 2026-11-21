using System.Windows.Media;
using AIHub.Services.ImageLayers;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class LayerCanvas
{
    private LayerCanvas? _viewportPeer;
    private LayerEdgeStroke[] _edgeStrokes = [];
    private readonly List<LayerEdgePoint> _guideDrag = [];

    internal bool KeepEdgeGuide { get; set; } = true;
    internal event Action<LayerEdgeStroke>? EdgeGuideDrawn;

    internal void LinkViewport(LayerCanvas other)
    {
        _viewportPeer = other;
        other._viewportPeer = this;
        UpdateViewport();
    }

    internal void ShowEdgeGuides(LayerEdgeStroke[] strokes)
    {
        _edgeStrokes = strokes;
        InvalidateVisual();
    }

    private void AppendGuidePoint(Point at)
    {
        var area = FocusArea;
        var pixel = PixelAt(at);
        var x = Math.Clamp((int)Math.Floor(pixel.X), area.Left, area.Right - 1);
        var y = Math.Clamp((int)Math.Floor(pixel.Y), area.Top, area.Bottom - 1);
        if (_guideDrag.Count > 0 && _guideDrag[^1].X == x && _guideDrag[^1].Y == y) return;
        _guideDrag.Add(new(x, y));
    }

    private void DrawEdgeGuides(DrawingContext drawing)
    {
        if (Tool != LayerCanvasTool.EdgeBrush) return;
        foreach (var stroke in _edgeStrokes) Draw(stroke.Keep, stroke.Points);
        Draw(KeepEdgeGuide, _guideDrag);

        void Draw(bool keep, IReadOnlyList<LayerEdgePoint> points)
        {
            if (points.Count == 0) return;
            var brush = keep ? Cyan : Brushes.OrangeRed;
            var width = Math.Max(2, Scale * (keep ? 1 : 3));
            Point At(LayerEdgePoint pixel) => ScreenAt(new(pixel.X + .5f, pixel.Y + .5f));
            var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (points.Count == 1) drawing.DrawEllipse(brush, null, At(points[0]), width / 2, width / 2);
            for (var i = 1; i < points.Count; i++) drawing.DrawLine(pen, At(points[i - 1]), At(points[i]));
        }
    }
}
