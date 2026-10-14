using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>A playback-only drawing layer. Never changes text, selection, caret or scroll position.</summary>
public sealed class MusicLyricsPlaybackAdorner : Adorner, IDisposable
{
    private readonly TextBox _editor;
    private int _start, _length;
    private bool _disposed;
    private Size _lastSize;
    private double _lastHorizontal, _lastVertical;
    private Brush? _lastAccent;
    public MusicLyricsPlaybackAdorner(TextBox editor) : base(editor)
    {
        _editor = editor; IsHitTestVisible = false; Focusable = false;
        editor.LayoutUpdated += LayoutChanged;
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }
    public void SetRange(int start, int length)
    {
        if (_disposed) return;
        if (start < 0 || length <= 0 || start > _editor.Text.Length - length) { start = 0; length = 0; }
        if (_start == start && _length == length) return;
        _start = start; _length = length; InvalidateVisual();
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e) => InvalidateVisual();
    private void LayoutChanged(object? sender, EventArgs e)
    {
        var accent = _editor.TryFindResource("AccentBrush") as Brush;
        if (_lastSize == _editor.RenderSize && _lastHorizontal == _editor.HorizontalOffset &&
            _lastVertical == _editor.VerticalOffset && ReferenceEquals(accent, _lastAccent)) return;
        _lastSize = _editor.RenderSize; _lastHorizontal = _editor.HorizontalOffset;
        _lastVertical = _editor.VerticalOffset; _lastAccent = accent; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_disposed || _length == 0 || _editor.LineCount == 0) return;
        var viewport = FindViewport(_editor);
        var bounds = viewport is null ? new Rect(_editor.RenderSize) :
            viewport.TransformToAncestor(_editor).TransformBounds(new Rect(viewport.RenderSize));
        drawing.PushClip(new RectangleGeometry(bounds));
        // Low opacity keeps glyphs, the selection and the caret legible in either theme.
        var accent = (_editor.TryFindResource("AccentBrush") as Brush ?? Brushes.RoyalBlue).CloneCurrentValue();
        accent.Opacity = .18;
        var outline = (_editor.TryFindResource("AccentBrush") as Brush ?? Brushes.RoyalBlue).CloneCurrentValue();
        outline.Opacity = .65;
        foreach (var rect in GetVisibleRectangles()) drawing.DrawRoundedRectangle(accent, new Pen(outline, 1), rect, 2, 2);
        drawing.Pop();
    }
    /// <summary>Visual rows, including soft wrapping; useful for geometry verification without mutating the editor.</summary>
    public IReadOnlyList<Rect> GetVisibleRectangles()
    {
        var result = new List<Rect>();
        if (_disposed || _length == 0 || _start > _editor.Text.Length - _length || _editor.LineCount == 0) return result;
        var end = _start + _length;
        var firstLine = _editor.GetLineIndexFromCharacterIndex(_start);
        var lastLine = _editor.GetLineIndexFromCharacterIndex(end - 1);
        if (firstLine < 0 || lastLine < 0) return result;
        for (var line = firstLine; line <= lastLine; line++)
        {
            var rowStart = _editor.GetCharacterIndexFromLineIndex(line);
            var rowEnd = Math.Min(_editor.Text.Length, rowStart + _editor.GetLineLength(line));
            var from = Math.Max(_start, rowStart); var to = Math.Min(end, rowEnd);
            while (to > from && _editor.Text[to - 1] is '\r' or '\n') to--;
            if (to <= from) continue;
            var left = _editor.GetRectFromCharacterIndex(from, false);
            var right = _editor.GetRectFromCharacterIndex(to - 1, true);
            if (left.IsEmpty || right.IsEmpty || left.Height <= 0) continue;
            var rect = new Rect(Math.Min(left.Left, right.Left), left.Top,
                Math.Max(2, Math.Abs(right.Left - left.Left)), left.Height);
            if (rect.Bottom >= 0 && rect.Top <= _editor.ActualHeight) result.Add(rect);
        }
        return result;
    }
    private static ScrollContentPresenter? FindViewport(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollContentPresenter viewport) return viewport;
            if (FindViewport(child) is { } found) return found;
        }
        return null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _length = 0; _editor.LayoutUpdated -= LayoutChanged;
        _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        InvalidateVisual();
    }
}
