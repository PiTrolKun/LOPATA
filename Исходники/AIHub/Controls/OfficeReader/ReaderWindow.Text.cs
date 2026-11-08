using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace AIHub.Controls.OfficeReader;

public sealed partial class ReaderWindow
{
    private readonly ReaderTextSelection _selection = new();
    private bool _copying;

    private async Task CopySelectedTextAsync()
    {
        var document = _document; var lifetime = _load;
        if (document is null || lifetime is null || !_selection.HasSelection || _copying) return;
        _copying = true;
        try
        {
            var text = await _selection.ReadAsync(document, lifetime.Token);
            if (!ReferenceEquals(document, _document) || _closed || lifetime.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text)) { _status.Text = T("NoText"); return; }
            System.Windows.Clipboard.SetText(text);
            _status.Text = T("Copied");
        }
        catch (OperationCanceledException) { }
        catch (ExternalException) { if (!_closed) _status.Text = T("CopyFailed"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (ReferenceEquals(document, _document) && !_closed) ShowFailure(error); }
        finally { _copying = false; }
    }

    internal void DragTextSelection(ReaderTextLayer origin, Point point)
    {
        var position = origin.TranslatePoint(point, _body);
        var scroll = _book ? _spreadScroll : FindScroll(_pages);
        if (scroll is not null)
        {
            if (position.Y < 25) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 22);
            else if (position.Y > _body.ActualHeight - 25) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 22);
        }
        var layers = TextLayers(_body).Where(p => p.IsVisible && p.PageText.Runs.Count > 0).ToArray();
        if (layers.Length == 0) return;
        var target = layers.MinBy(layer =>
        {
            var p = _body.TranslatePoint(position, layer);
            var dx = Math.Max(Math.Max(-p.X, p.X - layer.ActualWidth), 0);
            var dy = Math.Max(Math.Max(-p.Y, p.Y - layer.ActualHeight), 0);
            return dx * dx + dy * dy;
        })!;
        var local = _body.TranslatePoint(position, target);
        _selection.Extend(target.PageIndex, target.PageText.Hit(local.X / target.ActualWidth, local.Y / target.ActualHeight));
    }

    private static IEnumerable<ReaderTextLayer> TextLayers(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ReaderTextLayer layer) yield return layer;
            foreach (var nested in TextLayers(child)) yield return nested;
        }
    }
}
