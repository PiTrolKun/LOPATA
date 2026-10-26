using System.Windows;
using System.Windows.Controls;
using AIHub.Services.VideoEditor;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoTimeline
{
    internal Func<string, string> HelpText { get; set; } = key => key;
    private void UpdateHoverHelp(Point at)
    {
        ToolTipService.SetIsEnabled(this, _pressPoint is null);
        if (_pressPoint is not null) return;
        var block = Blocks().LastOrDefault(b => b.Bounds.Contains(at)); string message;
        if (block is null) {
            var row = (int)((at.Y - Ruler) / Row);
            message = at.Y >= Ruler && row >= 0 && row < Project.Tracks.Count && Project.Tracks[row].Kind == "audio" && at.X < Header ? HelpText("TrackVolume") : HelpText("Timeline");
        }
        else if (block.Item.Subtitle) {
            var n = Project.Narration.Single(n => n.Id == block.Item.Id); message = VideoSeries.Label(Project, n, SeriesText) + "\n" + HelpText("SubtitleClip");
        }
        else {
            var track = Project.Tracks.Single(t => t.Id == block.RowId); var clip = track.Clips.Single(c => c.Id == block.Item.Id);
            var edge = Math.Abs(at.X - block.Bounds.Left) < 8 || Math.Abs(at.X - block.Bounds.Right) < 8;
            message = VideoSeries.Label(Project, track, clip, SeriesText) + "\n" + HelpText(edge ? track.Kind == "audio" && Project.StretchAudio ? "StretchEdge" : "TrimEdge" : track.Kind == "audio" ? "AudioClip" : "VideoClip");
        }
        if (ToolTip is System.Windows.Controls.ToolTip old && old.Content is TextBlock text && text.Text == message) return;
        ToolTip = new System.Windows.Controls.ToolTip { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 } };
    }
}
