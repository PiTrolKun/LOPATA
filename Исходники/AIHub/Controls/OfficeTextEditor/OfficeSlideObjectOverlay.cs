using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.OfficeEditor;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeSlideObjectOverlay : FrameworkElement
{
    private static readonly Pen Edge = FrozenPen(Brushes.DodgerBlue, 1.6);
    private static readonly Pen Halo = FrozenPen(Brushes.White, 3.8);
    private static readonly Pen EmptyFrame = FrozenPen(Brushes.SlateGray, 1, DashStyles.Dash);
    private readonly string _canvasName;
    private readonly Dictionary<OfficeSlideObject, Rect> _pendingSamples = new();
    private OfficeSlideCanvasModel? _slide;
    private BitmapSource? _bitmap;
    private Rect _viewport = Rect.Empty;
    private Rect _original, _candidate;
    private Point _screenAnchor;
    private Matrix _fromDevice;
    private Grip _gesture;
    private bool _dragging;
    internal OfficeSlideObject? Selected { get; private set; }
    internal event Action? SelectionChanged;
    internal event Action? Edited;
    internal event Action? ReplaceRequested;
    internal event Action? EditRequested;
    internal Rect ObjectRect(OfficeSlideObject item) => ToScreen(item.Bounds);

    internal OfficeSlideObjectOverlay(Func<string,string> localize)
    {
        Focusable = true;
        ClipToBounds = true;
        _canvasName = localize("OfficeEditor.SlideObjectCanvas");
        AutomationProperties.SetName(this, _canvasName);
        AutomationProperties.SetAutomationId(this, "Office.Native.SlideCanvas");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static Pen FrozenPen(System.Windows.Media.Brush brush, double width, DashStyle? dashes = null)
    {
        var pen = new Pen(brush, width);
        if (dashes != null) pen.DashStyle = dashes;
        pen.Freeze();
        return pen;
    }

    internal void SetSlide(OfficeSlideCanvasModel? model)
    {
        if (ReferenceEquals(_slide, model)) return;
        CancelGesture();
        _slide = model;
        _bitmap = null;
        _pendingSamples.Clear();
        Select(null);
        InvalidateVisual();
    }

    internal void SetViewport(Rect renderedSlideBounds)
    {
        if (_viewport == renderedSlideBounds) return;
        CancelGesture();
        _viewport = renderedSlideBounds;
        InvalidateVisual();
    }

    internal void SetBitmap(BitmapSource? bitmap)
    {
        if (!ReferenceEquals(_bitmap, bitmap)) _pendingSamples.Clear();
        _bitmap = bitmap;
        InvalidateVisual();
    }

    internal void Select(OfficeSlideObject? item)
    {
        if (ReferenceEquals(Selected, item)) return;
        Selected = item;
        AutomationProperties.SetName(this, item == null ? _canvasName : _canvasName + " · " + item.Name);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!Ready) return;
        dc.PushClip(new RectangleGeometry(_viewport));
        foreach (var item in _slide!.Objects)
        {
            if (!item.ShowEmptyOutline || ReferenceEquals(item, Selected)) continue;
            var box = ToScreen(item.Bounds);
            if (Valid(box))
            {
                dc.PushTransform(Rotation(box, item));
                dc.DrawRectangle(null, EmptyFrame, box);
                dc.Pop();
            }
        }
        // Keep direct manipulation visible until the new full-fidelity slide arrives.
        foreach (var (item,sample) in _pendingSamples)
        {
            if (!_dragging || !ReferenceEquals(item,Selected)) DrawSample(dc,sample,ToScreen(item.Bounds));
        }
        if (Selected != null)
        {
            var bounds = _dragging ? _candidate : ToScreen(Selected.Bounds);
            if (Valid(bounds))
            {
                if (_dragging && (Selected.Angle?.Invoke() ?? 0) == 0) DrawSample(dc, _pendingSamples.TryGetValue(Selected,out var source) ?
                    source : RelativeSample(_original), bounds);
                dc.PushTransform(Rotation(bounds, Selected));
                DrawSelection(dc, bounds);
                dc.Pop();
            }
        }
        dc.Pop();
    }

    private Rect RelativeSample(Rect screen) => new((screen.X - _viewport.X) / _viewport.Width,
        (screen.Y - _viewport.Y) / _viewport.Height, screen.Width / _viewport.Width, screen.Height / _viewport.Height);

    private void DrawSample(DrawingContext dc, Rect region, Rect destination)
    {
        if (_bitmap == null || !Valid(destination)) return;
        var brush = new ImageBrush(_bitmap) { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = region, Stretch = Stretch.Fill, Opacity = .78 };
        dc.DrawRectangle(brush, null, destination);
    }

    private static void DrawSelection(DrawingContext dc, Rect bounds)
    {
        dc.DrawRectangle(null, Halo, bounds);
        dc.DrawRectangle(null, Edge, bounds);
        foreach (var grip in Grips)
        {
            var center = GripCenter(grip, bounds);
            dc.DrawRectangle(Brushes.White, Edge, new Rect(center.X - 3.5, center.Y - 3.5, 7, 7));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Ready) return;
        var point = e.GetPosition(this);
        var screenAnchor = PointToScreen(point);
        Focus();
        var grip = HitGrip(point);
        if (grip == Grip.None)
        {
            Select(HitObject(point));
            if (Selected == null) return;
            grip = Grip.Move;
        }
        e.Handled = true;
        if (e.ClickCount == 2 && grip == Grip.Move && Selected!.TextBlocks != null)
        { EditRequested?.Invoke(); return; }
        if (e.ClickCount == 2 && grip == Grip.Move && Selected!.CanReplacePicture && ReplaceRequested != null)
        {
            ReplaceRequested.Invoke();
            return;
        }
        _original = ToScreen(Selected!.Bounds);
        _candidate = _original;
        _screenAnchor = screenAnchor;
        _fromDevice = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        if (CaptureMouse()) _gesture = grip;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_gesture != Grip.None)
        {
            UpdateGesture(point);
            e.Handled = true;
        }
        else
        {
            var grip = HitGrip(point);
            if (grip == Grip.None && HitObject(point) != null) grip = Grip.Move;
            Cursor = CursorFor(grip);
        }
    }

    private void UpdateGesture(Point point)
    {
        // Focusing or selecting an object can reposition this layer during capture.
        // Use one screen origin and the scale at mouse-down for the whole gesture.
        var delta = _fromDevice.Transform(PointToScreen(point) - _screenAnchor);
        if (!_dragging && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        var angle = Selected?.Angle?.Invoke() ?? 0;
        if (_gesture == Grip.Move || angle == 0) _candidate = Adjust(_original, _gesture, delta);
        else
        {
            var local = new RotateTransform(-angle).Value.Transform(delta);
            var sized = Adjust(_original, _gesture, local);
            var center = Rotation(_original, Selected!).Transform(Center(sized));
            _candidate = new Rect(center.X - sized.Width/2, center.Y - sized.Height/2, sized.Width, sized.Height);
        }
        Cursor = CursorFor(_gesture);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture == Grip.None) return;
        UpdateGesture(e.GetPosition(this));
        var commit = _dragging && _original != _candidate;
        var result = _candidate;
        CancelGesture();
        if (commit) Commit(result);
        e.Handled = true;
    }

    private void Commit(Rect rect)
    {
        if (Selected == null || !Ready) return;
        if (_bitmap != null && (Selected.Angle?.Invoke() ?? 0) == 0) _pendingSamples.TryAdd(Selected, RelativeSample(ToScreen(Selected.Bounds)));
        Selected.SetBounds(new OfficeSlideBounds((rect.X - _viewport.X) * _slide!.Width / _viewport.Width,
            (rect.Y - _viewport.Y) * _slide.Height / _viewport.Height,
            rect.Width * _slide.Width / _viewport.Width, rect.Height * _slide.Height / _viewport.Height));
        InvalidateVisual();
        Edited?.Invoke();
    }

    private void CancelGesture()
    {
        _gesture = Grip.None;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture != Grip.None) CancelGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            if (_gesture != Grip.None) CancelGesture();
            else if (Selected != null) Select(null);
            else return;
            e.Handled = true;
            return;
        }
        if (!Ready || _gesture != Grip.None || (Keyboard.Modifiers & ~ModifierKeys.Shift) != 0) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.Tab)
        {
            var objects = _slide!.Objects;
            var index = Selected == null ? (shift ? objects.Count : -1) : objects.ToList().IndexOf(Selected);
            var next = shift ? index - 1 : index + 1;
            if (next >= 0 && next < objects.Count) { Select(objects[next]); e.Handled = true; }
            return;
        }
        if (Selected == null) return;
        double step = shift ? 10 : 1;
        var delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => new Vector()
        };
        if (delta.LengthSquared == 0) return;
        var original = ToScreen(Selected.Bounds);
        var moved = Adjust(original, Grip.Move, delta);
        if (original != moved) Commit(moved);
        e.Handled = true;
    }
}
