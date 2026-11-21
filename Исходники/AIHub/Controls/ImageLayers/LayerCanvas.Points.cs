using System.Windows;
using System.Windows.Input;
using AIHub.Services.ImageLayers;
using Point = System.Windows.Point;
using PixelPoint = System.Drawing.PointF;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class LayerCanvas
{
    private void StartPointGesture(Point at, ModifierKeys modifiers)
    {
        Focus();
        var point = NearestHandle(at);
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selectBox = true;
            _extendBox = true;
        }
        else if (point is { } handle)
        {
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                if (!_selectedPoints.Remove(handle)) _selectedPoints.Add(handle);
                InvalidateVisual();
                return;
            }
            _resetSelectionOnClick = _selectedPoints.Contains(handle) && _selectedPoints.Count > 1;
            if (!_selectedPoints.Contains(handle)) SelectOnly(handle);
            _dragAnchor = handle;
        }
        else if (NearestVertex(at) is { } vertex)
            _insertPoint = vertex;
        else if (CanPan)
        {
            StartPan(at, MouseButton.Left);
            return;
        }
        else
            _selectBox = true;

        _gestureStart = at;
        CaptureMouse();
        InvalidateVisual();
    }

    private void CompletePointGesture(Point start, Point at)
    {
        var dragged = Dragged(start, at);
        var anchor = _dragAnchor;
        var insert = _insertPoint;
        var selectBox = _selectBox;
        var extend = _extendBox;
        var resetSelection = _resetSelectionOnClick;
        ClearGesture();
        if (!CanEditPoints || _mask is not { } mask || _source is not { } source) return;

        if (selectBox)
        {
            if (!extend) _selectedPoints.Clear();
            if (dragged)
            {
                var bounds = new Rect(start, at);
                foreach (var point in VisibleControls())
                    if (bounds.Contains(ScreenAt(Vertex(point)))) _selectedPoints.Add(point);
            }
            else if (extend && NearestHandle(at) is { } point)
                _selectedPoints.Add(point);
            InvalidateVisual();
        }
        else if (anchor is { } held)
        {
            if (dragged)
            {
                var poses = _selectedPoints.ToDictionary(point => point, point => MaskPosition(point, Translate(point, at - start)));
                if (LayerContourPoints.Move(mask, source, _spacing, _rings, _handles, poses, LayerDocument.PointsKind) is { } edit)
                    ContourEdited?.Invoke(edit);
            }
            else if (resetSelection) SelectOnly(held);
        }
        else if (!dragged && insert is { } added)
        {
            if (Array.BinarySearch(_handles[added.Ring], added.Vertex) >= 0) SelectOnly(added);
            else ContourEdited?.Invoke(LayerContourPoints.Insert(mask, _spacing, _rings, _handles, added));
        }
    }

    private void RemoveSelected()
    {
        if (_source is not { } source || _mask is not { } mask || _selectedPoints.Count == 0) return;
        ClearGesture();
        var edit = LayerContourPoints.Remove(mask, source, _spacing, _rings, _handles, _selectedPoints);
        ContourEdited?.Invoke(edit);
    }

    internal void SelectPoints(IEnumerable<LayerControlPoint> points)
    {
        _selectedPoints.Clear();
        _selectedPoints.UnionWith(points);
        InvalidateVisual();
    }

    private void SelectOnly(LayerControlPoint point) => SelectPoints([point]);

    private IEnumerable<LayerControlPoint> VisibleControls()
    {
        var showEvery = Math.Max(1, (int)Math.Ceiling(4d / (Scale * _spacing)));
        for (var ring = 0; ring < _handles.Length; ring++)
            for (var index = 0; index < _handles[ring].Length; index++)
            {
                var point = new LayerControlPoint(ring, _handles[ring][index]);
                if (index % showEvery == 0 || _selectedPoints.Contains(point))
                    yield return point;
            }
    }

    private LayerControlPoint? NearestHandle(Point at)
    {
        LayerControlPoint? result = null;
        var best = 5.5 * 5.5;
        foreach (var point in VisibleControls())
        {
            var distance = (ScreenAt(Vertex(point)) - at).LengthSquared;
            if (distance >= best) continue;
            best = distance;
            result = point;
        }
        return result;
    }

    private LayerControlPoint? NearestVertex(Point at)
    {
        LayerControlPoint? result = null;
        var best = 6d * 6;
        for (var ring = 0; ring < _outline.Length; ring++)
        {
            var vertices = _outline[ring];
            for (var i = 0; i < vertices.Length; i++)
            {
                var a = ScreenAt(vertices[i]);
                var b = ScreenAt(vertices[(i + 1) % vertices.Length]);
                var segment = b - a;
                var length = segment.LengthSquared;
                var t = length == 0 ? 0 : Math.Clamp(Vector.Multiply(at - a, segment) / length, 0, 1);
                var distance = (at - (a + t * segment)).LengthSquared;
                if (distance >= best) continue;
                best = distance;
                result = new(ring, t <= .5 ? i : (i + 1) % vertices.Length);
            }
        }
        return result;
    }

    private PixelPoint Vertex(LayerControlPoint point) => _outline[point.Ring][point.Vertex];

    private PixelPoint MaskPosition(LayerControlPoint point, PixelPoint target)
    {
        // Apply the visible change to the raster anchor used by the project's point history.
        var visible = Vertex(point);
        var raster = _rings[point.Ring][point.Vertex];
        return Clamp(new(raster.X + (target.X - visible.X), raster.Y + (target.Y - visible.Y)));
    }

    private PixelPoint Translate(LayerControlPoint point, Vector drag)
    {
        var original = Vertex(point);
        return Clamp(new(original.X + (float)(drag.X / Scale), original.Y + (float)(drag.Y / Scale)));
    }
}
