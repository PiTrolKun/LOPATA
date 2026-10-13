using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RichTextBox = System.Windows.Controls.RichTextBox;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace AIHub.Controls;

/// <summary>Follow only the bottom reader; preserve a stable block anchor across size/document changes.</summary>
internal sealed class MusicPoetryChatViewport
{
    private readonly RichTextBox _chat;
    private readonly Func<IReadOnlyList<(string Key, Block Block)>> _blocks;
    private bool _follow = true, _restoring, _scheduled;
    private int _userRevision;
    private Snapshot? _pending;
    private sealed record Snapshot(bool Follow, double Offset, string? Key, double Top, int UserRevision);
    internal bool Following => _follow;

    internal MusicPoetryChatViewport(RichTextBox chat, Func<IReadOnlyList<(string Key, Block Block)>> blocks)
    {
        _chat = chat; _blocks = blocks;
        chat.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => {
            if (OwnViewer(e.OriginalSource as DependencyObject) && !_restoring && _pending is null
                && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
            { ++_userRevision; _follow = AtBottom(); }
        }), true);
        chat.PreviewMouseWheel += (_, e) => { ++_userRevision; if (e.Delta > 0) _follow = false; };
        chat.PreviewKeyDown += (_, e) => {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            { ++_userRevision; if (e.Key is Key.Up or Key.PageUp or Key.Home) _follow = false; }
        };
        chat.PreviewMouseLeftButtonDown += (_, e) => {
            var node = e.OriginalSource as DependencyObject;
            while (node is Visual && node != chat)
            {
                if (node is ScrollBar or Thumb) { ++_userRevision; _follow = false; break; }
                node = VisualTreeHelper.GetParent(node);
            }
        };
    }

    internal void Reset() { _pending = null; _follow = true; ++_userRevision; }
    internal void Change(Action change)
    {
        if (_pending is null || _pending.UserRevision != _userRevision) _pending = Capture();
        _restoring = true;
        try { change(); }
        finally { _restoring = false; }
        ScheduleRestore();
    }
    internal void PreserveAfterExpansion()
    { _follow = false; ++_userRevision; _pending = Capture(); ScheduleRestore(); }

    private bool AtBottom() => _chat.ExtentHeight - _chat.ViewportHeight - _chat.VerticalOffset <= 3;
    private bool OwnViewer(DependencyObject? node)
    {
        while (node is Visual)
        {
            if (node is TextBoxBase text) return ReferenceEquals(text, _chat);
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
    private Snapshot Capture()
    {
        string? key = null; double top = 0;
        foreach (var block in _blocks())
        {
            if (!block.Block.ContentStart.HasValidLayout) continue;
            var start = block.Block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var end = block.Block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            if (!start.IsEmpty && (!end.IsEmpty ? end.Bottom : start.Bottom) >= 0)
            { key = block.Key; top = start.Top; break; }
        }
        return new(_follow, _chat.VerticalOffset, key, top, _userRevision);
    }
    private void ScheduleRestore()
    {
        if (_scheduled) return;
        _scheduled = true;
        _chat.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            _scheduled = false; var snapshot = _pending; _pending = null;
            if (snapshot is null || snapshot.UserRevision != _userRevision || !_chat.IsLoaded) return;
            _restoring = true;
            try
            {
                _chat.UpdateLayout();
                if (snapshot.Follow) _chat.ScrollToEnd();
                else
                {
                    var anchor = _blocks().FirstOrDefault(b => b.Key == snapshot.Key).Block;
                    var rect = anchor?.ContentStart.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
                    _chat.ScrollToVerticalOffset(rect.IsEmpty ? snapshot.Offset : _chat.VerticalOffset + rect.Top - snapshot.Top);
                }
                _chat.UpdateLayout(); _follow = snapshot.Follow;
            }
            finally { _restoring = false; }
        }));
    }
}
