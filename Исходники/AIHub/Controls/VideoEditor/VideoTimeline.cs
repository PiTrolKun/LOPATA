using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.VideoEditor;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Cursors = System.Windows.Input.Cursors;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoTimeline : FrameworkElement
{
    public VideoProject Project { get; set; } = new();
    public double Zoom { get; set; } = 55;
    public double Position { get; set; }
    public bool Snap { get; set; } = true;
    internal Func<string, string> SeriesText { get; set; } = key => key.Replace("Series", "", StringComparison.Ordinal);
    public string? SelectedClip { get => Selection.Primary is { Subtitle: false } item ? item.Id : null; set => Selection.Replace(value is null ? [] : [new(value)]); }
    public string? SelectedTrack { get; set; }
    public Dictionary<string, ImageSource> Thumbnails { get; } = [];
    public Dictionary<string, VideoWaveform> Waveforms { get; } = [];
    public event Action<double>? SeekRequested;
    public event Action<string?, string?>? SelectionChanged;
    public event Action<Action<VideoProject>>? EditRequested;
    public event Action<string, string?, Point>? VolumeRequested;
    private double _delta;
    private (string Track, string? Clip, bool ClipOnly)? _volumePress;
    private const double Header = 145, Row = 65, Ruler = 30;
    public void Refresh() { VideoSeries.Ensure(Project); Selection.Resolve = roots => Project.SelectBySeries ? VideoSeries.Expand(Project, roots) : SynchronizeSelection ? VideoSelectionMap.Expand(Project, roots) : roots.ToArray(); RefreshSubtitleRows(); Selection.Prune(Project); Width = Math.Max(900, Header + (Project.Duration + 5) * Zoom); Height = Ruler + (Project.Tracks.Count + Math.Max(1, _subtitleRows.Length)) * Row + 30; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(13, 21, 38)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var step = Zoom > 120 ? 1 : Zoom > 35 ? 5 : 10;
        for (var t = 0; t <= Project.Duration + 5; t += step) { var x = Header + t * Zoom; dc.DrawLine(new Pen(Brushes.SlateGray, .5), new(x, 22), new(x, ActualHeight)); Text(dc, TimeSpan.FromSeconds(t).ToString(@"mm\:ss"), x + 3, 3, 11, Brushes.SlateGray); }
        for (var i = 0; i < Project.Tracks.Count; i++)
        {
            var track = Project.Tracks[i]; var y = Ruler + i * Row;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 31, 49)), new Pen(Brushes.DarkSlateGray, .5), new Rect(0, y, Header - 2, Row));
            Text(dc, track.Name, 8, y + 7, 12, track.Id == SelectedTrack ? Brushes.DeepSkyBlue : Brushes.WhiteSmoke);
            if (track.Kind == "audio") {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 41, 59)), new Pen(Brushes.SteelBlue, .7), new Rect(6, y + 27, Header - 14, 28), 4, 4);
                Text(dc, (track.Locked ? "🔒 " : "") + (track.Muted ? "○ " : "● ") + (track.Volume * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%  ▾", 12, y + 33, 12, Brushes.LightSkyBlue);
            }
            else Text(dc, (track.Locked ? "🔒 " : "") + (track.Hidden ? "○ " : "● ") + "V", 8, y + 32, 11, Brushes.SlateGray);
            foreach (var clip in track.Clips)
            {
                var x = Header + clip.Start * Zoom; var w = Math.Max(4, clip.Duration * Zoom); var selected = Selection.Contains(new(clip.Id));
                if (_dragging.Contains(new(clip.Id))) { if (_mode == "move") x += _delta * Zoom; if (_mode == "left") { x += _delta * Zoom; w -= _delta * Zoom; } if (_mode == "right") w += _delta * Zoom; }
                var rect = new Rect(x, y + 4, Math.Max(3, w), Row - 8);
                dc.PushClip(new RectangleGeometry(rect));
                var brush = VideoEditing.TooLong(Project, clip) ? Brushes.IndianRed : track.Kind == "audio" ? new SolidColorBrush(Color.FromRgb(28, 107, 103)) : new SolidColorBrush(Color.FromRgb(38, 75, 137));
                dc.DrawRoundedRectangle(brush, new Pen(selected ? Brushes.White : Brushes.SlateGray, selected ? 2 : .5), rect, 3, 3);
                if (clip.AssetId is {} assetId)
                {
                    var asset = Project.Asset(assetId);
                    if (track.Kind == "video" && Thumbnails.TryGetValue(assetId, out var thumbnail)) for (double tx = x; tx < x + w; tx += 72) dc.DrawImage(thumbnail, new Rect(tx, y + 23, 70, 32));
                    if (track.Kind == "audio" && Waveforms.TryGetValue(assetId, out var wave) && wave.Count > 0) DrawWaveform(dc, clip, x, w, y, wave);
                    SeriesLabel(dc, VideoSeries.Label(Project, track, clip, SeriesText), clip.SeriesNumbers, x + 6, y + 7, w - 12);
                }
                else SeriesLabel(dc, VideoSeries.Label(Project, track, clip, SeriesText), clip.SeriesNumbers, x + 6, y + 8, w - 12);
                if (selected) { dc.DrawRectangle(Brushes.White, null, new Rect(x, y + 13, 4, 25)); dc.DrawRectangle(Brushes.White, null, new Rect(x + w - 4, y + 13, 4, 25)); }
                dc.Pop();
            }
        }
        DrawSubtitles(dc); DrawSelectionBox(dc);
        var cursor = Header + Position * Zoom; dc.DrawLine(new Pen(Brushes.DeepSkyBlue, 2), new(cursor, 0), new(cursor, ActualHeight));
    }
    private void Text(DrawingContext dc, string s, double x, double y, double size, Brush color) => dc.DrawText(new FormattedText(s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    private void SeriesLabel(DrawingContext dc, string label, IEnumerable<int> series, double x, double y, double width)
    {
        if (width < 12) return;
        double Measure(string value) => new FormattedText(value, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
        if (Measure(label) > width) {
            var suffix = VideoSeries.Suffix(series); var prefix = label[..^suffix.Length];
            while (prefix.Length > 0 && Measure(prefix + "…" + suffix) > width) prefix = prefix[..^1];
            label = prefix.Length == 0 ? suffix : prefix + "…" + suffix;
        }
        Text(dc, label, x, y, 11, Brushes.White);
    }
}
