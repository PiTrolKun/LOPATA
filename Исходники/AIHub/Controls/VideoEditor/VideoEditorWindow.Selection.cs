using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Services.VideoEditor;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    private bool _syncSelection;
    private void TimelineSelectionChanged()
    {
        if (_syncSelection) return; _syncSelection = true;
        try {
            SyncSelectedText();
            _selectedSegment = _timeline.Selection.Primary is { Subtitle: true } item ? item.Id : null;
            LoadSelectedText(); ShowProperties(); Safe(UpdateFramingAsync);
            if (!_busy && _timeline.Selection.Count > 1) _status.Text = string.Format(T("SelectedElements"), _timeline.Selection.Count);
            else if (!_busy && _selectedSegment is null) _status.Text = T("Ready");
        }
        finally { _syncSelection = false; }
    }
    private void SegmentSelectionChanged(SelectionChangedEventArgs args)
    {
        if (_syncSelection || _loadingText) return; _syncSelection = true;
        try {
            _selectedSegment = args.AddedItems.Cast<NarrationSegment>().LastOrDefault()?.Id ?? _segments.SelectedItems.Cast<NarrationSegment>().LastOrDefault()?.Id;
            var selected = _segments.SelectedItems.Cast<NarrationSegment>().Select(n => new VideoItemId(n.Id, true));
            var removed = args.RemovedItems.Cast<NarrationSegment>().Select(n => new VideoItemId(n.Id, true)).ToHashSet();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) selected = _timeline.Selection.Roots.Where(i => !i.Subtitle && !(_timeline.Selection.Resolve?.Invoke([i]) ?? [i]).Any(removed.Contains)).Concat(selected);
            _timeline.SelectItems(selected.ToArray(), _selectedSegment is {} id ? new(id, true) : null);
            SyncSelectedText();
            LoadSelectedText(); ShowProperties(); Safe(UpdateFramingAsync);
        }
        finally { _syncSelection = false; }
    }
    private void SyncSelectedText()
    {
        var wanted = Project.Narration.Where(n => _timeline.Selection.Contains(new(n.Id, true))).ToArray();
        foreach (var n in _segments.SelectedItems.Cast<NarrationSegment>().Where(n => !wanted.Contains(n)).ToArray()) _segments.SelectedItems.Remove(n);
        foreach (var n in wanted.Where(n => !_segments.SelectedItems.Contains(n))) _segments.SelectedItems.Add(n);
        if (!_segments.IsKeyboardFocusWithin && wanted.FirstOrDefault() is {} first) _segments.ScrollIntoView(first);
    }
    private string[] SelectedFragments() => _timeline.Selection.Items.Where(i => i.Subtitle).Select(i => i.Id).Concat(_segments.SelectedItems.Cast<NarrationSegment>().Select(n => n.Id)).Distinct().ToArray();
    private void SplitSelection() => Edit(p => VideoSelectionEditing.Split(p, _timeline.Selection.Items, _position));
    private void UnlinkSelection() => Edit(p => { foreach (var c in VideoSelectionEditing.Clips(p, _timeline.Selection.Items)) c.LinkId = null; });
}
