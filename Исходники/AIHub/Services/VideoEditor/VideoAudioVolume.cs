using System.IO;

namespace AIHub.Services.VideoEditor;

internal static class VideoAudioVolume
{
    internal static void Set(VideoProject project, string trackId, string? clipId, double percent)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 200) throw new InvalidDataException("VideoEditor.VolumeRange");
        var track = project.Tracks.Single(t => t.Id == trackId);
        if (track.Kind != "audio") throw new InvalidOperationException("VideoEditor.SelectAudio");
        if (track.Locked) throw new InvalidOperationException("VideoEditor.VolumeLocked");
        if (clipId is null) track.Volume = percent / 100;
        else track.Clips.Single(c => c.Id == clipId).Volume = percent / 100;
        project.ModifiedUtc = DateTimeOffset.UtcNow;
    }
}
