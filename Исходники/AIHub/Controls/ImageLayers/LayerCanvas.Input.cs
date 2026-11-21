using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.ImageLayers;
using Point = System.Windows.Point;
using PixelPoint = System.Drawing.PointF;
using PixelRect = System.Drawing.Rectangle;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;

namespace AIHub.Controls.ImageLayers;

internal sealed partial class LayerCanvas
{
    private Point? _panDrag, _gestureStart, _rubberBand, _regionMenuPress;
    private MouseButton? _panButton;
    private LayerControlPoint? _dragAnchor, _insertPoint;
    private bool _selectBox, _extendBox, _resetSelectionOnClick;
    private Vector _pointDrag;
    private readonly HashSet<LayerControlPoint> _selectedPoints = [];
    private readonly List<PixelPoint> _stroke = [];
    private bool CanPan => !ViewLocked && _zoom > 1.0001;
    private bool CanEditPoints => !ReadOnly && _view == LayerCanvasView.Original && Tool == LayerCanvasTool.Points && _rings.Count > 0;
    internal event Action<Point>? RegionActionsRequested;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (PressPointer(e.GetPosition(this), e.ChangedButton, Keyboard.Modifiers, Keyboard.IsKeyDown(Key.Space)))
            e.Handled = true;
    }

    internal bool PressPointer(Point at, MouseButton button, ModifierKeys modifiers, bool panModifier = false)
    {
        if (_source is null || _panDrag is not null) return false;
        if (button is MouseButton.Left or MouseButton.Middle or MouseButton.Right) StopLayerFocus();
        if (button == MouseButton.Middle && !ViewLocked && _gestureStart is null)
        {
            StartPan(at, MouseButton.Middle);
            return true;
        }
        if (button == MouseButton.Right)
        {
            _regionMenuPress = null;
            if (CanEditPoints && (NearestHandle(at) ?? _dragAnchor) is { } point)
            {
                Focus();
                if (!_selectedPoints.Contains(point)) SelectOnly(point);
                RemoveSelected();
                return true;
            }
            if (!CanOpenRegionActions(at)) return false;
            _regionMenuPress = at;
            Focus();
            return true;
        }
        if (button != MouseButton.Left || _gestureStart is not null) return false;
        if (CanPan && (Tool == LayerCanvasTool.Hand || ReadOnly || _view != LayerCanvasView.Original || panModifier))
        {
            StartPan(at, MouseButton.Left);
            return true;
        }
        if (ReadOnly || _view != LayerCanvasView.Original) return false;
        if (Tool == LayerCanvasTool.Markers)
        {
            var pixel = PixelAt(at);
            var x = (int)Math.Floor(pixel.X);
            var y = (int)Math.Floor(pixel.Y);
            if (!FocusArea.Contains(x, y)) return false;
            Focus();
            MarkerPlaced?.Invoke(new(x, y, true));
            return true;
        }
        if (Tool == LayerCanvasTool.Points && CanEditPoints)
        {
            StartPointGesture(at, modifiers);
            return true;
        }
        if (Tool == LayerCanvasTool.EdgeBrush)
        {
            if (!ScreenRect(FocusArea).Contains(at)) return false;
            Focus();
            CaptureMouse();
            _gestureStart = at;
            AppendGuidePoint(at);
            return true;
        }
        if (!ScreenRect(_source.Frame).Contains(at)) return false;
        if (Tool == LayerCanvasTool.Region) _rubberBand = at;
        else if (Tool == LayerCanvasTool.Lasso)
        {
            if (CanCloseLassoContour && (at - ScreenAt(_lassoPath[0])).Length <= 7)
            {
                CloseLassoContour();
                return true;
            }
            BeginLassoStep(at);
        }
        else if (Tool == LayerCanvasTool.Pencil && _rings.Count > 0) _stroke.Add(Clamp(PixelAt(at)));
        else return false;

        Focus();
        _gestureStart = at;
        CaptureMouse();
        return true;
    }

    private void StartPan(Point at, MouseButton button)
    {
        Focus();
        UpdateLassoGuide(null);
        _panDrag = at;
        _panButton = button;
        Cursor = Cursors.ScrollAll;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Tool == LayerCanvasTool.Lasso && _gestureStart is not null && e.LeftButton != MouseButtonState.Pressed)
            return;
        MovePointer(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Tool != LayerCanvasTool.Lasso || _gestureStart is not null || _lassoCursor is null) return;
        UpdateLassoGuide(null);
        InvalidateVisual();
    }

    internal void MovePointer(Point at)
    {
        if (_panDrag is { } previous)
        {
            _pan += at - previous;
            _panDrag = at;
            UpdateViewport();
        }
        else if (_gestureStart is { } start)
        {
            if (Tool == LayerCanvasTool.Lasso) ContinueLassoStep(at);
            else if (Tool == LayerCanvasTool.EdgeBrush) AppendGuidePoint(at);
            else if (_rubberBand is not null) _rubberBand = at;
            else if (_dragAnchor is not null && Dragged(start, at)) _pointDrag = at - start;
            else if (_insertPoint is not null && CanPan && Dragged(start, at))
            {
                ClearGesture(release: false);
                StartPan(start, MouseButton.Left);
                _pan += at - start;
                _panDrag = at;
                UpdateViewport();
            }
            else if (_selectBox && Dragged(start, at)) _pointDrag = at - start;
            else if (_stroke.Count > 0 && (at - ScreenAt(_stroke[^1])).Length >= 2) _stroke.Add(Clamp(PixelAt(at)));
            InvalidateVisual();
        }
        else if (Tool == LayerCanvasTool.Lasso && !ReadOnly && _view == LayerCanvasView.Original)
        {
            UpdateLassoGuide(at);
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (ReleasePointer(e.GetPosition(this), e.ChangedButton)) e.Handled = true;
    }

    internal bool ReleasePointer(Point at, MouseButton button)
    {
        if (button == MouseButton.Right && _regionMenuPress is { } pressed)
        {
            _regionMenuPress = null;
            if (!Dragged(pressed, at) && CanOpenRegionActions(at)) RegionActionsRequested?.Invoke(at);
            return true;
        }
        if (_panButton == button)
        {
            if (_panDrag is { } previous)
            {
                _pan += at - previous;
                UpdateViewport();
            }
            ClearGesture();
            return true;
        }
        if (button != MouseButton.Left || _gestureStart is not { } start || _source is not { } source) return false;
        if (Tool == LayerCanvasTool.Points)
        {
            CompletePointGesture(start, at);
        }
        else if (Tool == LayerCanvasTool.EdgeBrush)
        {
            AppendGuidePoint(at);
            var stroke = new LayerEdgeStroke(KeepEdgeGuide, [.. _guideDrag]);
            ClearGesture();
            EdgeGuideDrawn?.Invoke(stroke);
        }
        else if (Tool == LayerCanvasTool.Lasso)
        {
            EndLassoStep(at);
            ClearGesture();
            UpdateLassoGuide(at);
        }
        else
        {
            var region = _rubberBand is not null ? PixelRectangle(start, at) : PixelRect.Empty;
            var stroke = _stroke.ToArray();
            ClearGesture();
            if (region.Width > 1 && region.Height > 1)
                RegionSelected?.Invoke(region);
            else if (_mask is { } mask && stroke.Length >= 2 && (at - start).Length >= 2)
            {
                var candidate = _outline.Select((ring, index) => (index, distance: ring.Min(p =>
                    (ScreenAt(p) - ScreenAt(LayerContours.Nearest(p, stroke))).LengthSquared)))
                    .MinBy(item => item.distance);
                var changes = LayerContours.Pencil(_outline[candidate.index], _handles[candidate.index], stroke, (float)(18 / Scale));
                if (changes.Count != 0 && LayerContourPoints.Move(mask, source, _spacing, _rings, _handles, changes.ToDictionary(
                    entry => new LayerControlPoint(candidate.index, entry.Key),
                    entry => MaskPosition(new(candidate.index, entry.Key), entry.Value)),
                    LayerDocument.PencilKind) is { } edit)
                    ContourEdited?.Invoke(edit);
            }
        }
        return true;
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.Key, e.IsRepeat)) e.Handled = true;
    }

    internal bool HandleKey(Key key, bool repeated = false)
    {
        if (HandleLassoKey(key, repeated)) return true;
        if (key == Key.Delete && CanEditPoints && !_selectBox && _selectedPoints.Count > 0)
        {
            if (!repeated) RemoveSelected();
            return true;
        }
        if (key == Key.Escape && (_gestureStart is not null || _panDrag is not null))
        {
            ClearGesture();
            return true;
        }
        if (key == Key.Escape && Tool == LayerCanvasTool.Points && _selectedPoints.Count > 0)
        {
            SelectPoints([]);
            return true;
        }
        return false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_source is null || _gestureStart is not null || _panDrag is not null || ViewLocked) return;
        Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        ClearGesture(release: false);
    }

    private PixelPoint Clamp(PixelPoint point) => _source is { } source
        ? new(Math.Clamp(point.X, 0, source.Width), Math.Clamp(point.Y, 0, source.Height)) : point;

    private PixelRect PixelRectangle(Point a, Point b)
    {
        var p = Clamp(PixelAt(a)); var q = Clamp(PixelAt(b));
        return PixelRect.FromLTRB((int)Math.Floor(Math.Min(p.X, q.X)), (int)Math.Floor(Math.Min(p.Y, q.Y)),
            (int)Math.Ceiling(Math.Max(p.X, q.X)), (int)Math.Ceiling(Math.Max(p.Y, q.Y)));
    }

    private static bool Dragged(Point start, Point end) =>
        Math.Abs(end.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(end.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private bool CanOpenRegionActions(Point at) => !ReadOnly && _gestureStart is null && _panDrag is null
        && RegionActionsRequested is not null && _scope is { } area && Viewport.Contains(at) && ScreenRect(area).Contains(at);

    private void ClearGesture(bool release = true)
    {
        DiscardUnfinishedLassoStep();
        _panDrag = _gestureStart = _rubberBand = _regionMenuPress = null;
        _panButton = null;
        _dragAnchor = _insertPoint = null;
        _pointDrag = default;
        _selectBox = _extendBox = _resetSelectionOnClick = false;
        _stroke.Clear();
        _guideDrag.Clear();
        ClearValue(CursorProperty);
        if (release && IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void DrawGesture(DrawingContext drawing)
    {
        var pen = new Pen(Cyan, 1.5);
        if (_pointDrag.LengthSquared > 0)
        {
            if (_selectBox && _gestureStart is { } start)
                drawing.DrawRectangle(ScopeBrush, pen, new Rect(start, start + _pointDrag));
            else if (_dragAnchor is not null)
                foreach (var handle in _selectedPoints)
                {
                    var initial = ScreenAt(Vertex(handle));
                    var destination = ScreenAt(Translate(handle, _pointDrag));
                    drawing.DrawLine(pen, initial, destination);
                    drawing.DrawEllipse(Cyan, null, destination, 4, 4);
                }
        }
        for (var i = 1; i < _stroke.Count; i++)
            drawing.DrawLine(pen, ScreenAt(_stroke[i - 1]), ScreenAt(_stroke[i]));
    }
}
