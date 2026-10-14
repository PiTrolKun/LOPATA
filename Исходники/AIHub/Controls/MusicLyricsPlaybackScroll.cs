using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

/// <summary>Center each new sung phrase; paused reading and manual scrolling within a phrase stay free.</summary>
public sealed class MusicLyricsPlaybackScroll : IDisposable
{
    private readonly TextBox _editor;
    private bool _playing, _needsFollow = true, _disposed;
    private int _start = -1, _length;
    public MusicLyricsPlaybackScroll(TextBox editor)
    {
        _editor = editor;
        editor.SizeChanged += Resized;
    }
    public void Resume() => _needsFollow = true;
    public void Clear() { _start = -1; _length = 0; _playing = false; _needsFollow = true; }
    public void Follow(int start, int length, bool playing = true)
    {
        if (_disposed) return;
        if (_start != start || _length != length || playing && !_playing) _needsFollow = true;
        _start = start; _length = length; _playing = playing;
        if (!playing || !_needsFollow || start < 0 || length <= 0 || start > _editor.Text.Length - length || _editor.LineCount == 0) return;
        var first = _editor.GetRectFromCharacterIndex(start);
        var last = _editor.GetRectFromCharacterIndex(start + length - 1, true);
        if (first.IsEmpty) {
            var line = _editor.GetLineIndexFromCharacterIndex(start);
            if (line >= 0) _editor.ScrollToLine(line);
            return; // The next player update centers the now-laid-out phrase.
        }
        var viewport = Viewport(_editor) ?? new Rect(0, 0, _editor.ViewportWidth, _editor.ViewportHeight);
        if (viewport.Height <= 0) return;
        var bottom = last.IsEmpty ? first.Bottom : last.Bottom;
        // An oversized phrase cannot fit: keep its first singing row visible rather than hiding it.
        var center = bottom - first.Top <= viewport.Height ? (first.Top + bottom) / 2 : (first.Top + first.Bottom) / 2;
        var desired = _editor.VerticalOffset + center - (viewport.Top + viewport.Height / 2);
        var maximum = Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight);
        _editor.ScrollToVerticalOffset(Math.Clamp(desired, 0, maximum));
        _needsFollow = false;
    }
    private Rect? Viewport(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollContentPresenter presenter)
                return presenter.TransformToAncestor(_editor).TransformBounds(new Rect(new System.Windows.Size(presenter.ActualWidth, presenter.ActualHeight)));
            if (Viewport(child) is { } found) return found;
        }
        return null;
    }
    private void Resized(object sender, SizeChangedEventArgs e) => _needsFollow = true;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _editor.SizeChanged -= Resized;
    }
}
