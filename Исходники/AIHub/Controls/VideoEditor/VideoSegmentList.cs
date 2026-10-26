using System.Windows;
using System.Windows.Controls;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ListBox = System.Windows.Controls.ListBox;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.VideoEditor;

internal sealed class VideoSegmentList : ListBox
{
    private Point? _start;
    private object[] _initial = [];
    private SelectionAdorner? _adorner;
    private ScrollViewer? _viewport;
    private VideoSelectionScroll? _scroll;
    private double _startOffset;
    internal bool BoxSelectionMode { get; set; }
    internal Func<ScrollViewer, Point> SelectionPointer { get; set; } = scroll => Mouse.GetPosition(scroll);
    internal VideoSegmentList() { SelectionMode = System.Windows.Controls.SelectionMode.Extended; ScrollViewer.SetCanContentScroll(this, false); Unloaded += (_, _) => EndBox(); }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != this; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ScrollBar) { base.OnPreviewMouseLeftButtonDown(e); return; }
        if (ItemsControl.ContainerFromElement(this, e.OriginalSource as DependencyObject) is not null && !BoxSelectionMode && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { base.OnPreviewMouseLeftButtonDown(e); return; }
        BeginBox(e.GetPosition(this), Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true;
    }
    internal void BeginBox(Point at, bool additive)
    {
        Focus(); _start = at; _initial = additive ? SelectedItems.Cast<object>().ToArray() : [];
        _viewport ??= FindViewport(this); _startOffset = _viewport?.VerticalOffset ?? 0;
        if (_viewport is {} viewport) _scroll = new(viewport, () => SelectionPointer(viewport), () => UpdateBox(viewport.TranslatePoint(SelectionPointer(viewport), this)));
        if (!additive) SelectedItems.Clear(); CaptureMouse();
        _adorner = new(this); AdornerLayer.GetAdornerLayer(this)?.Add(_adorner);
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_start is not null) { UpdateBox(e.GetPosition(this)); e.Handled = true; } else base.OnMouseMove(e);
    }
    internal void UpdateBox(Point at)
    {
        if (_start is not {} start) return;
        if ((at - start).Length > SystemParameters.MinimumVerticalDragDistance) _scroll?.Start();
        start.Y -= (_viewport?.VerticalOffset ?? 0) - _startOffset;
        var box = new Rect(start, at); var selected = new HashSet<object>(_initial);
        foreach (var item in Items) if (ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement row && new Rect(row.TranslatePoint(new(), this), row.RenderSize).IntersectsWith(box)) selected.Add(item);
        foreach (var item in SelectedItems.Cast<object>().Where(i => !selected.Contains(i)).ToArray()) SelectedItems.Remove(item);
        foreach (var item in selected.Where(i => !SelectedItems.Contains(i))) SelectedItems.Add(item);
        if (_adorner is not null) { _adorner.Box = box; _adorner.InvalidateVisual(); }
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e) { if (_start is not null) { EndBox(); e.Handled = true; } else base.OnPreviewMouseLeftButtonUp(e); }
    internal void EndBox()
    {
        _scroll?.Stop(); _scroll = null;
        _start = null; if (_adorner is not null) AdornerLayer.GetAdornerLayer(this)?.Remove(_adorner); _adorner = null; ReleaseMouseCapture();
    }
    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e) { if (Mouse.Captured != this) EndBox(); base.OnLostMouseCapture(e); }
    private static ScrollViewer? FindViewport(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (FindViewport(VisualTreeHelper.GetChild(parent, i)) is {} found) return found;
        return null;
    }
    private sealed class SelectionAdorner(UIElement owner) : Adorner(owner)
    {
        internal Rect Box { get; set; }
        protected override void OnRender(DrawingContext drawingContext) { drawingContext.PushClip(new RectangleGeometry(new Rect(AdornedElement.RenderSize))); drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 38, 160, 255)), new Pen(Brushes.DeepSkyBlue, 1), Box); drawingContext.Pop(); }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
}
