using System.Windows.Input;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoTimeline
{
    internal bool VolumePointer(Point point, bool clipOnly, bool pressed)
    {
        if (pressed) {
            _volumePress = null;
            if (VolumeTargetAt(point, clipOnly) is not {} target) return false;
            _volumePress = (target.Track, target.Clip, clipOnly); CaptureMouse();
            SelectedTrack = target.Track; SelectedClip = target.Clip; SelectionChanged?.Invoke(SelectedClip, SelectedTrack); InvalidateVisual(); return true;
        }
        if (_volumePress is not {} pending || pending.ClipOnly != clipOnly) return false;
        _volumePress = null; ReleaseMouseCapture();
        if (VolumeTargetAt(point, clipOnly) is {} released && released.Track == pending.Track && released.Clip == pending.Clip)
            // Open after the complete initiating click. Otherwise Popup's capture treats
            // its mouse-up at the rounded corner/outside as a dismissal.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => RequestVolumeAt(point, clipOnly));
        return true;
    }
    internal bool RequestVolumeAt(Point point, bool clipOnly)
    {
        if (VolumeTargetAt(point, clipOnly) is not {} target) return false;
        SelectedTrack = target.Track; SelectedClip = target.Clip;
        SelectionChanged?.Invoke(SelectedClip, SelectedTrack); InvalidateVisual();
        VolumeRequested?.Invoke(target.Track, target.Clip, point); return true;
    }
    private (string Track, string? Clip)? VolumeTargetAt(Point point, bool clipOnly)
    {
        var row = (int)((point.Y - Ruler) / Row);
        if (point.Y < Ruler || row < 0 || row >= Project.Tracks.Count || point.X < 0) return null;
        var track = Project.Tracks[row]; if (track.Kind != "audio") return null;
        var clip = point.X < Header ? null : track.Clips.LastOrDefault(c => point.X >= Header + c.Start * Zoom && point.X <= Header + (c.Start + c.Duration) * Zoom);
        if (clipOnly ? clip is null : point.X >= Header - 8 || (point.Y - Ruler) % Row is < 27 or > 55) return null;
        return (track.Id, clipOnly ? clip!.Id : null);
    }
}
