using System.Windows;
using AIHub.Services.VideoEditor;
using Point = System.Windows.Point;

namespace AIHub.Controls.VideoEditor;

internal sealed partial class VideoEditorWindow
{
    internal VideoVolumePopup? VolumePopup { get; private set; }
    private bool _volumeClosing;
    private async Task OpenVolumeAsync(string trackId, string? clipId, Point anchor)
    {
        CloseVolumePopup();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); VideoProjectStore.Save(RecoveryProject, VideoProjectStore.Recovery(Project.Id)); _segments.Items.Refresh(); await RefreshPreviewAsync(); }
        if (_closed) return;
        var track = Project.Tracks.Single(t => t.Id == trackId); var clip = clipId is null ? null : track.Clips.Single(c => c.Id == clipId);
        var enabled = !_busy && !track.Locked; var edited = false;
        var name = clip is null ? track.Name : clip.AssetId is {} id ? Project.Asset(id).Name : clip.Text;
        var title = string.Format(T(clip is null ? "TrackVolumeTitle" : "ClipVolumeTitle"), name);
        var hint = T(_busy ? "Busy" : track.Locked ? "VolumeLocked" : clip is null ? "TrackVolumeHint" : "ClipVolumeHint");
        var popup = new VideoVolumePopup(Resources, T, title, (clip?.Volume ?? track.Volume) * 100, enabled, hint, percent => {
            if (_busy || _closed) return;
            try {
                void Set(VideoProject p) => VideoAudioVolume.Set(p, trackId, clipId, percent);
                if (!edited) { _editing.Edit(Set); edited = true; } else Set(Project);
                _dirty = true; _timeline.Project = Project; _timeline.InvalidateVisual(); ShowProperties(); UpdateTitle();
                if (!_volumeClosing) Safe(async () => await _preview.SetVolumesAsync(Project));
            }
            catch (Exception error) { _status.Text = Error(error); }
        }) { PlacementTarget = _timeline, HorizontalOffset = anchor.X, VerticalOffset = anchor.Y };
        popup.Closed += (_, _) => {
            if (VolumePopup == popup) VolumePopup = null;
            if (!edited || _closed) return;
            try { VideoProjectStore.Save(Project, VideoProjectStore.Recovery(Project.Id)); }
            catch (Exception error) { _status.Text = T("RecoveryFailed") + " " + Error(error); }
        };
        VolumePopup = popup; popup.IsOpen = true;
    }
    private void CloseVolumePopup(bool closingWindow = false)
    {
        if (VolumePopup is not {} popup) return;
        _volumeClosing = closingWindow;
        try { popup.TryApplyText(); popup.IsOpen = false; VolumePopup = null; }
        finally { _volumeClosing = false; }
    }
}
