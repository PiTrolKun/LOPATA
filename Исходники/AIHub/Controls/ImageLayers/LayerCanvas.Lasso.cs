using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.ImageLayers;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using PixelPoint = System.Drawing.PointF;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class LayerCanvas
{
    private sealed record LassoContour(PixelPoint[] Path, int OpenLength, int[] Steps);

    private readonly List<PixelPoint> _lassoPath = [];
    private readonly Stack<int> _lassoSteps = [];
    private readonly List<LassoContour> _lassoContours = [];
    private int? _lassoPressIndex;
    private Point? _lassoCursor;
    private Point _lastLassoSample;
    private PixelPoint[] _lassoGuide = [];
    private Geometry? _lassoUnion;
    private bool _lassoMagnetic;

    internal event Action<PixelPoint[][]>? LassoSelected;
    internal event Action? LassoChanged;
    internal bool HasLassoSelection => _lassoPath.Count > 0 || _lassoContours.Count > 0;
    internal bool CanCloseLassoContour => _lassoPressIndex is null && _lassoPath.Count >= 3;
    internal bool CanFinishLasso => CanCloseLassoContour || _lassoPath.Count == 0 && _lassoContours.Count > 0;
    internal bool LassoMagnetic
    {
        get => _lassoMagnetic;
        set
        {
            if (_lassoMagnetic == value) return;
            _lassoMagnetic = value;
            UpdateLassoGuide(_lassoCursor);
            NotifyLassoChanged();
        }
    }

    private int LassoRadius => Math.Clamp((int)Math.Round(9 / Scale), 2, 12);

    private void BeginLassoStep(Point at)
    {
        _lassoPressIndex = _lassoPath.Count;
        _lastLassoSample = at;
        AppendLasso(ToLassoPoint(at), followEdge: true);
        UpdateLassoGuide(null);
        NotifyLassoChanged();
    }

    private void ContinueLassoStep(Point at, bool final = false)
    {
        var distance = (at - _lastLassoSample).Length;
        if (distance < (final ? .25 : 2)) return;
        var next = ToLassoPoint(at);
        var previous = _lassoPath[^1];
        var gap = Math.Max(Math.Abs(next.X - previous.X), Math.Abs(next.Y - previous.Y));
        AppendLasso(next, followEdge: gap > 6);
        _lastLassoSample = at;
    }

    private void EndLassoStep(Point at)
    {
        ContinueLassoStep(at, final: true);
        if (_lassoPressIndex is int oldCount && oldCount < _lassoPath.Count) _lassoSteps.Push(oldCount);
        _lassoPressIndex = null;
        NotifyLassoChanged();
    }

    private PixelPoint ToLassoPoint(Point at)
    {
        var point = Clamp(PixelAt(at));
        return LassoMagnetic && _source is { } source ? LayerMagneticPath.Snap(source, point, LassoRadius) : point;
    }

    private void AppendLasso(PixelPoint point, bool followEdge)
    {
        if (_lassoPath.Count > 0 && _lassoPath[^1] == point) return;
        if (followEdge && LassoMagnetic && _lassoPath.Count > 0 && _source is { } source)
            _lassoPath.AddRange(LayerMagneticPath.Trace(source, _lassoPath[^1], point, LassoRadius));
        else
            _lassoPath.Add(point);
    }

    internal void CloseLassoContour()
    {
        if (Tool != LayerCanvasTool.Lasso || ReadOnly || !CanCloseLassoContour) return;
        var openLength = _lassoPath.Count;
        AppendLasso(_lassoPath[0], followEdge: true);
        _lassoContours.Add(new([.. _lassoPath], openLength, [.. _lassoSteps.Reverse()]));
        _lassoPath.Clear();
        _lassoSteps.Clear();
        UpdateLassoGuide(null);
        RebuildLassoUnion();
        NotifyLassoChanged();
    }

    internal void FinishLasso()
    {
        if (Tool != LayerCanvasTool.Lasso || ReadOnly || !CanFinishLasso) return;
        CloseLassoContour();
        LassoSelected?.Invoke(_lassoContours.Select(contour => contour.Path).ToArray());
    }

    internal void ResetLasso()
    {
        _lassoPath.Clear();
        _lassoSteps.Clear();
        _lassoContours.Clear();
        _lassoPressIndex = null;
        _lassoUnion = null;
        UpdateLassoGuide(null);
        NotifyLassoChanged();
    }

    private void DiscardUnfinishedLassoStep()
    {
        if (_lassoPressIndex is not int start) return;
        _lassoPath.RemoveRange(start, _lassoPath.Count - start);
        _lassoPressIndex = null;
        UpdateLassoGuide(null);
        NotifyLassoChanged();
    }

    private bool HandleLassoKey(Key key, bool repeat)
    {
        if (Tool != LayerCanvasTool.Lasso || ReadOnly || _view != LayerCanvasView.Original) return false;
        if (key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!repeat) LassoMagnetic = !LassoMagnetic;
            return true;
        }
        if (_gestureStart is not null || _panDrag is not null) return false;
        if (key == Key.Enter && CanFinishLasso)
        {
            if (!repeat) FinishLasso();
            return true;
        }
        if (key == Key.Escape && HasLassoSelection)
        {
            if (!repeat) ResetLasso();
            return true;
        }
        if (key != Key.Back || !HasLassoSelection) return false;
        if (!repeat)
        {
            if (_lassoSteps.TryPop(out var start))
                _lassoPath.RemoveRange(start, _lassoPath.Count - start);
            else if (_lassoContours.Count > 0)
            {
                var last = _lassoContours[^1];
                _lassoContours.RemoveAt(_lassoContours.Count - 1);
                _lassoPath.AddRange(last.Path.Take(last.OpenLength));
                foreach (var step in last.Steps) _lassoSteps.Push(step);
                RebuildLassoUnion();
            }
            UpdateLassoGuide(_lassoCursor);
            NotifyLassoChanged();
        }
        return true;
    }

    private void UpdateLassoGuide(Point? cursor)
    {
        _lassoCursor = cursor;
        _lassoGuide = [];
        if (cursor is not { } at || _lassoPath.Count == 0 || _source is not { } source
            || !Viewport.Contains(at) || !ScreenRect(source.Frame).Contains(at)) return;
        var point = ToLassoPoint(at);
        _lassoGuide = LassoMagnetic
            ? LayerMagneticPath.Trace(source, _lassoPath[^1], point, LassoRadius) : [point];
    }

    private void RebuildLassoUnion()
    {
        _lassoUnion = null;
        foreach (var contour in _lassoContours)
        {
            var geometry = Geometry([contour.Path]);
            _lassoUnion = _lassoUnion is { } previous
                ? System.Windows.Media.Geometry.Combine(previous, geometry, GeometryCombineMode.Union, null) : geometry;
        }
        _lassoUnion?.Freeze();
    }

    private void NotifyLassoChanged()
    {
        InvalidateVisual();
        LassoChanged?.Invoke();
    }

    private void DrawLasso(DrawingContext drawing)
    {
        if (Tool != LayerCanvasTool.Lasso || _view != LayerCanvasView.Original) return;
        if (_lassoUnion is { } union)
        {
            drawing.PushTransform(SourceTransform());
            drawing.DrawGeometry(ScopeBrush, new Pen(Cyan, 1.5 / Scale), union);
            drawing.Pop();
        }
        if (_lassoPath.Count == 0) return;
        var pen = new Pen(Cyan, 1.5);
        for (var i = 1; i < _lassoPath.Count; i++)
            drawing.DrawLine(pen, ScreenAt(_lassoPath[i - 1]), ScreenAt(_lassoPath[i]));
        var guidePen = new Pen(Cyan, 1.3) { DashStyle = DashStyles.Dash };
        var previous = ScreenAt(_lassoPath[^1]);
        foreach (var point in _lassoGuide)
        {
            var end = ScreenAt(point);
            drawing.DrawLine(guidePen, previous, end);
            previous = end;
        }
        drawing.DrawEllipse(null, pen, ScreenAt(_lassoPath[0]), 5, 5);
    }
}
