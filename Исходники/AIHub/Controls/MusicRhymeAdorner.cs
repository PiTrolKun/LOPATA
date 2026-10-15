using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Models;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace AIHub.Controls;

/// <summary>Coloured glyphs over the original TextBox: no document conversion, background fill or undo mutation.</summary>
public sealed class MusicRhymeAdorner : Adorner, IDisposable
{
    private readonly TextBox _editor;
    private MusicRhymeLine[] _lines = [];
    private Brush _brush = Brushes.Green;
    private bool _disposed;
    private string _layout = "";
    public MusicRhymeAdorner(TextBox editor) : base(editor)
    {
        _editor = editor; IsHitTestVisible = false;
        editor.LayoutUpdated += Layout; editor.SelectionChanged += Selection;
        editor.TextChanged += TextChanged;
    }
    public void Set(MusicRhymeAnalysis? analysis, string color)
    {
        var issues = analysis?.Issues.Where(i => i.Kind is "Rhyme" or "Rhythm").Select(i => i.Line).ToHashSet() ?? [];
        _lines = analysis?.Lines.Where(l => issues.Contains(l.Number)).ToArray() ?? [];
        _brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); InvalidateVisual();
    }
    private void TextChanged(object sender, TextChangedEventArgs e) { _lines = []; InvalidateVisual(); }
    private void Selection(object sender, RoutedEventArgs e) => InvalidateVisual();
    private void Layout(object? sender, EventArgs e)
    {
        var key = $"{_editor.RenderSize}:{_editor.VerticalOffset}:{_editor.HorizontalOffset}:{_editor.FontSize}:{_editor.FontFamily}:{_editor.FontWeight}:{_editor.FontStyle}:{_editor.FlowDirection}:{VisualTreeHelper.GetDpi(_editor)}:{_editor.Foreground}";
        if (_layout == key) return; _layout = key; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_disposed || _editor.LineCount == 0 || _lines.Length == 0) return;
        var viewport = FindViewport(_editor);
        var bounds = viewport is null ? new Rect(_editor.RenderSize)
            : viewport.TransformToAncestor(_editor).TransformBounds(new Rect(viewport.RenderSize));
        drawing.PushClip(new RectangleGeometry(bounds));
        var first = _editor.GetFirstVisibleLineIndex(); var last = _editor.GetLastVisibleLineIndex();
        foreach (var line in _lines)
        {
            if (line.Start > _editor.Text.Length - line.Length || _editor.Text.Substring(line.Start, line.Length) != line.Text) continue;
            for (var row = Math.Max(first, _editor.GetLineIndexFromCharacterIndex(line.Start)); row <= last; row++)
            {
                var start = Math.Max(line.Start, _editor.GetCharacterIndexFromLineIndex(row));
                if (start >= line.Start + line.Length) break;
                var end = Math.Min(line.Start + line.Length, _editor.GetCharacterIndexFromLineIndex(row) + _editor.GetLineLength(row));
                while (end > start && _editor.Text[end - 1] is '\r' or '\n') end--;
                if (end <= start) continue;
                // Native selection foreground and the caret remain on top of their own visual regions.
                if (_editor.SelectionLength > 0 && start < _editor.SelectionStart + _editor.SelectionLength && end > _editor.SelectionStart) continue;
                var rectangle = _editor.GetRectFromCharacterIndex(start);
                if (rectangle.IsEmpty) continue;
                var text = new FormattedText(_editor.Text[start..end], CultureInfo.CurrentUICulture, _editor.FlowDirection,
                    new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight, _editor.FontStretch),
                    _editor.FontSize, _brush, VisualTreeHelper.GetDpi(_editor).PixelsPerDip);
                drawing.DrawText(text, new(rectangle.Left, rectangle.Top));
            }
        }
        drawing.Pop();
    }
    private static ScrollContentPresenter? FindViewport(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is ScrollContentPresenter p) return p; if (FindViewport(child) is { } found) return found; }
        return null;
    }
    public void Dispose()
    { _disposed = true; _editor.LayoutUpdated -= Layout; _editor.SelectionChanged -= Selection; _editor.TextChanged -= TextChanged; _lines = []; }
}
