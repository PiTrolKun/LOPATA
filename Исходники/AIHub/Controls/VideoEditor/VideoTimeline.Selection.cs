using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using AIHub.Services.VideoEditor;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

internal sealed record VideoTimelineBlock(VideoItemId Item, string RowId, Rect Bounds, double Start, double End);
internal sealed partial class VideoTimeline
{
    internal VideoSelection Selection { get; } = new();
    internal bool SynchronizeSelection { get; set; } = true;
    private VideoSelectionScroll? _selectionScroll;
    internal Func<System.Windows.Controls.ScrollViewer, Point> SelectionPointer { get; set; } = scroll => System.Windows.Input.Mouse.GetPosition(scroll);
    internal void AttachViewport(System.Windows.Controls.ScrollViewer scroll)
    {
        _viewport = scroll; scroll.ScrollChanged += (_, _) => InvalidateVisual();
        SetBinding(MinWidthProperty, new Binding("ViewportWidth") { Source = scroll });
        SetBinding(MinHeightProperty, new Binding("ViewportHeight") { Source = scroll });
        _selectionScroll?.Stop();
        _selectionScroll = new(scroll, () => SelectionPointer(scroll), () => SelectionMove(scroll.TranslatePoint(SelectionPointer(scroll), this)));
        Unloaded += (_, _) => _selectionScroll?.Stop();
    }
    internal string SubtitleLabel { get; set; } = "Subtitles";
    internal string EmptySubtitleLabel { get; set; } = "…";
    private (string Key, NarrationSegment[] Cues)[] _subtitleRows = [];
    private Rect? _selectionBox;
    private readonly HashSet<VideoItemId> _dragging = [];
    private string? _mode;
    internal static string SubtitleKey(NarrationSegment n) => n.Language + ":" + n.SubtitleTrackId;
    private void RefreshSubtitleRows()
    {
        var cues = VideoSubtitles.Cues(Project).Where(n => n.End > n.Start).ToArray();
        foreach (var cue in cues) { var n = Project.Narration.First(n => n.Id == cue.Id); n.DisplayStart = cue.Start; n.DisplayEnd = cue.End; n.DisplayName = VideoSeries.Label(Project, n, SeriesText); }
        _subtitleRows = cues.GroupBy(SubtitleKey).Select(g => (g.Key, g.OrderBy(n => n.Start).ToArray())).ToArray();
    }
    internal VideoTimelineBlock[] Blocks()
    {
        var blocks = new List<VideoTimelineBlock>();
        for (var i = 0; i < Project.Tracks.Count; i++) foreach (var c in Project.Tracks[i].Clips)
            blocks.Add(new(new(c.Id), Project.Tracks[i].Id, new(Header + c.Start * Zoom, Ruler + i * Row + 4, Math.Max(4, c.Duration * Zoom), Row - 8), c.Start, c.Start + c.Duration));
        for (var i = 0; i < _subtitleRows.Length; i++) foreach (var n in _subtitleRows[i].Cues)
            blocks.Add(new(new(n.Id, true), _subtitleRows[i].Key, new(Header + n.Start * Zoom, Ruler + (Project.Tracks.Count + i) * Row + 4, Math.Max(4, (n.End - n.Start) * Zoom), Row - 8), n.Start, n.End));
        return blocks.ToArray();
    }
    private void DrawSubtitles(DrawingContext dc)
    {
        for (var i = 0; i < Math.Max(1, _subtitleRows.Length); i++) {
            var y = Ruler + (Project.Tracks.Count + i) * Row;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(33, 29, 48)), new Pen(Brushes.DarkSlateGray, .5), new Rect(0, y, Header - 2, Row));
            Text(dc, SubtitleLabel, 8, y + 7, 12, Brushes.Plum);
            if (i >= _subtitleRows.Length) continue;
            var row = _subtitleRows[i]; Text(dc, row.Cues[0].Language + " · " + row.Cues[0].SubtitleTitle, 8, y + 32, 11, Brushes.SlateGray);
            foreach (var n in row.Cues) {
                var x = Header + n.Start * Zoom; var w = Math.Max(4, (n.End - n.Start) * Zoom); var item = new VideoItemId(n.Id, true);
                if (_dragging.Contains(item)) { if (_mode == "move") x += _delta * Zoom; if (_mode == "left") { x += _delta * Zoom; w -= _delta * Zoom; } if (_mode == "right") w += _delta * Zoom; }
                var rect = new Rect(x, y + 4, Math.Max(3, w), Row - 8); dc.PushClip(new RectangleGeometry(rect));
                var selected = Selection.Contains(item);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(93, 65, 128)), new Pen(selected ? Brushes.White : Brushes.MediumPurple, selected ? 2 : .7), rect, 3, 3);
                SeriesLabel(dc, VideoSeries.Label(Project, n, SeriesText), n.SeriesNumbers, x + 6, y + 9, w - 12);
                Text(dc, n.Start.ToString("0.##", CultureInfo.CurrentCulture) + "–" + n.End.ToString("0.##", CultureInfo.CurrentCulture), x + 6, y + 34, 10, Brushes.Lavender);
                if (selected) { dc.DrawRectangle(Brushes.White, null, new Rect(x, y + 13, 4, 25)); dc.DrawRectangle(Brushes.White, null, new Rect(x + w - 4, y + 13, 4, 25)); }
                dc.Pop();
            }
        }
    }
    private void DrawSelectionBox(DrawingContext dc)
    {
        if (_selectionBox is {} box) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(48, 38, 160, 255)), new Pen(Brushes.DeepSkyBlue, 1), box);
    }
    internal void SelectItems(IEnumerable<VideoItemId> items, VideoItemId? primary = null)
    {
        Selection.Replace(items, primary); var block = Blocks().FirstOrDefault(b => b.Item == Selection.Primary); SelectedTrack = block is { Item.Subtitle: false } ? block.RowId : null;
        NotifySelection();
    }
    private void NotifySelection() { SelectionChanged?.Invoke(SelectedClip, SelectedTrack); InvalidateVisual(); }
    internal void SelectAll() => SelectItems(Blocks().Select(b => b.Item));
}
