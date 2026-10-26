using System.Windows;
using System.Windows.Input;
using AIHub.Services.VideoEditor;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoTimeline
{
    internal bool BoxSelectionMode { get; set; }
    private Point? _pressPoint;
    private VideoTimelineBlock? _pressedBlock;
    private VideoItemId[] _selectionBeforeBox = [];
    private bool _boxing, _dragStarted, _collapseOnClick;
    private bool _stretching;
    private VideoItemId? _rangeAnchor;
    internal void CancelSelection()
    {
        _selectionScroll?.Stop();
        _pressPoint = null; _pressedBlock = null; _selectionBox = null; _dragging.Clear(); _delta = 0; _volumePress = null; ReleaseMouseCapture(); SelectItems([]);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); var at = e.GetPosition(this);
        if (VolumePointer(at, false, true)) { e.Handled = true; return; }
        SelectionDown(at, Keyboard.Modifiers); e.Handled = true;
    }
    internal void SelectionDown(Point at, ModifierKeys modifiers)
    {
        if (at.Y < Ruler && at.X >= Header) { SeekRequested?.Invoke(Math.Clamp((at.X - Header) / Zoom, 0, Project.Duration)); return; }
        var blocks = Blocks(); var hit = blocks.LastOrDefault(b => b.Bounds.Contains(at));
        var additive = modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Shift);
        if (at.X < Header && at.Y >= Ruler && at.Y < Ruler + (Project.Tracks.Count + Math.Max(1, _subtitleRows.Length)) * Row) {
            var row = (int)((at.Y - Ruler) / Row); Selection.Replace([]); SelectedTrack = row >= 0 && row < Project.Tracks.Count ? Project.Tracks[row].Id : null; NotifySelection(); return;
        }
        _pressPoint = at; _pressedBlock = hit; _delta = 0; _dragStarted = false; _collapseOnClick = false; _stretching = false; _mode = "move";
        if (hit is null || BoxSelectionMode || modifiers.HasFlag(ModifierKeys.Alt)) {
            _boxing = true; _pressedBlock = null; _selectionBeforeBox = additive ? Selection.Roots.ToArray() : [];
            if (!additive) Selection.Replace([]); CaptureMouse(); NotifySelection(); return;
        }
        if (modifiers.HasFlag(ModifierKeys.Control)) { Selection.Toggle(hit.Item); if (!Selection.Contains(hit.Item)) { _pressPoint = null; NotifySelection(); return; } }
        else if (modifiers.HasFlag(ModifierKeys.Shift) && _rangeAnchor is {} anchor && blocks.FirstOrDefault(b => b.Item == anchor) is {} from && from.RowId == hit.RowId) {
            Selection.Replace(Selection.Roots.Concat(blocks.Where(b => b.RowId == hit.RowId && b.Start >= Math.Min(from.Start, hit.Start) && b.Start <= Math.Max(from.Start, hit.Start)).Select(b => b.Item)).ToArray(), hit.Item);
        }
        else { _collapseOnClick = Selection.Count > 1 && Selection.Contains(hit.Item) && !additive; if (!Selection.Contains(hit.Item)) Selection.Replace([hit.Item]); Selection.Activate(hit.Item); }
        _rangeAnchor = hit.Item; SelectedTrack = hit.Item.Subtitle ? null : hit.RowId;
        var audioStretch = !hit.Item.Subtitle && Project.StretchAudio && Project.Tracks.Any(t => t.Id == hit.RowId && t.Kind == "audio");
        if (Selection.Count == 1 || audioStretch) _mode = Math.Abs(at.X - hit.Bounds.Left) < 8 ? "left" : Math.Abs(at.X - hit.Bounds.Right) < 8 ? "right" : "move";
        _stretching = audioStretch && _mode != "move";
        _dragging.Clear();
        if (!Project.Tracks.Any(t => t.Locked && t.Clips.Any(c => Selection.Contains(new(c.Id))))) {
            foreach (var c in VideoSelectionEditing.Clips(Project, Selection.Items, false)) _dragging.Add(new(c.Id));
            _dragging.UnionWith(Selection.Items.Where(i => i.Subtitle));
        }
        if (_stretching) { _dragging.Clear(); if (!Project.Tracks.Single(t => t.Id == hit.RowId).Locked) _dragging.Add(hit.Item); }
        CaptureMouse(); NotifySelection();
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        var at = e.GetPosition(this); SelectionMove(at);
        UpdateHoverHelp(at);
        if (_pressPoint is null) {
            var row = (int)((at.Y - Ruler) / Row);
            Cursor = at.Y >= Ruler && row >= 0 && row < Project.Tracks.Count && Project.Tracks[row].Kind == "audio" && at.X is >= 6 and < Header - 8 && (at.Y - Ruler) % Row is >= 27 and <= 55 ? Cursors.Hand : Cursors.Arrow;
        }
    }
    internal void SelectionMove(Point at)
    {
        if (_pressPoint is not {} down) return;
        var movement = at - down;
        if (!_dragStarted && Math.Abs(movement.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(movement.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStarted = true;
        if (_boxing) {
            _selectionScroll?.Start();
            _selectionBox = new Rect(down, at); Selection.Replace(_selectionBeforeBox.Concat(Blocks().Where(b => b.Bounds.IntersectsWith(_selectionBox.Value)).Select(b => b.Item)).ToArray()); NotifySelection(); return;
        }
        if (_pressedBlock is not {} hit || _dragging.Count == 0) return;
        _delta = Math.Round(movement.X / Zoom * Project.Fps) / Project.Fps;
        if (_mode == "move") {
            var target = hit.Start + _delta;
            if (Snap) { var near = Blocks().Where(b => !_dragging.Contains(b.Item)).SelectMany(b => new[] { b.Start, b.End }).Append(Position).OrderBy(p => Math.Abs(p - target)).First(); if (Math.Abs(near - target) * Zoom < 9) target = near; }
            _delta = Math.Max(target - hit.Start, -Blocks().Where(b => _dragging.Contains(b.Item)).Min(b => b.Start));
        }
        else if (hit.Item.Subtitle) _delta = _mode == "left" ? Math.Clamp(_delta, -hit.Start, hit.End - hit.Start - 1 / Project.Fps) : Math.Max(_delta, -hit.End + hit.Start + 1 / Project.Fps);
        else {
            var clip = Project.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == hit.Item.Id);
            if (_stretching) {
                var span = clip.Duration * clip.Speed; var min = Math.Max(1 / Project.Fps, span / 16); var max = span / .0625;
                _delta = _mode == "left" ? Math.Clamp(_delta, Math.Max(-clip.Start, clip.Duration - max), clip.Duration - min) : Math.Clamp(_delta, min - clip.Duration, max - clip.Duration);
            }
            else {
                if (_mode == "left") _delta = Math.Clamp(_delta, Math.Max(-clip.Start, -clip.SourceIn / clip.Speed), clip.Duration - 1 / Project.Fps);
                if (_mode == "right") { var max = clip.AssetId is {} id && Project.Asset(id) is { Kind: "video" or "audio" } a && !clip.Freeze ? (a.Duration - clip.SourceIn) / clip.Speed - clip.Duration : 86400; _delta = Math.Clamp(_delta, -clip.Duration + 1 / Project.Fps, Math.Max(0, max)); }
            }
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (VolumePointer(e.GetPosition(this), false, false)) { e.Handled = true; return; }
        SelectionUp(e.GetPosition(this)); e.Handled = true;
    }
    internal void SelectionUp(Point at)
    {
        _selectionScroll?.Stop();
        if (_pressPoint is null) return;
        var hit = _pressedBlock; var delta = _delta; var mode = _mode; var moved = _dragStarted; var boxing = _boxing; var collapse = _collapseOnClick; var stretch = _stretching;
        var selection = Selection.Items.ToArray();
        _pressPoint = null; _pressedBlock = null; _boxing = false; _selectionBox = null; _dragging.Clear(); _delta = 0; ReleaseMouseCapture();
        if (boxing) { if (!moved) SeekRequested?.Invoke(Math.Clamp((at.X - Header) / Zoom, 0, Project.Duration)); NotifySelection(); return; }
        if (hit is null) return;
        var row = (int)((at.Y - Ruler) / Row); var target = row >= 0 && row < Project.Tracks.Count ? Project.Tracks[row].Id : null;
        if (moved && (Math.Abs(delta) > .0001 || mode == "move" && selection.Length == 1 && !hit.Item.Subtitle && target is not null && target != hit.RowId)) {
            EditRequested?.Invoke(p => {
                if (mode == "move") VideoSelectionEditing.Move(p, selection, delta, target);
                else if (stretch) VideoAudioStretch.Resize(p, hit.Item.Id, hit.Start + (mode == "left" ? delta : 0), hit.End - hit.Start + (mode == "left" ? -delta : delta));
                else if (hit.Item.Subtitle) VideoSubtitles.SetRange(p, hit.Item.Id, hit.Start + (mode == "left" ? delta : 0), hit.End + (mode == "right" ? delta : 0));
                else { var c = p.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == hit.Item.Id); VideoEditing.SetTiming(p, c.Id, c.Start + (mode == "left" ? delta : 0), c.SourceIn + (mode == "left" && !c.Freeze ? delta * c.Speed : 0), c.Duration + (mode == "left" ? -delta : delta), c.Speed); }
            });
        }
        else if (collapse) { Selection.Replace([hit.Item]); NotifySelection(); }
        InvalidateVisual();
    }
    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        _selectionScroll?.Stop();
        if (Mouse.Captured != this) { _volumePress = null; _pressPoint = null; _selectionBox = null; _dragging.Clear(); InvalidateVisual(); }
        base.OnLostMouseCapture(e);
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) { if (VolumePointer(e.GetPosition(this), true, true)) e.Handled = true; else base.OnMouseRightButtonDown(e); }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { if (VolumePointer(e.GetPosition(this), true, false)) e.Handled = true; else base.OnMouseRightButtonUp(e); }
}
