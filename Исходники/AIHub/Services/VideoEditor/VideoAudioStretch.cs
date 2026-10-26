using System.IO;

namespace AIHub.Services.VideoEditor;

internal static class VideoAudioStretch
{
    internal static void Resize(VideoProject p, string id, double start, double duration)
    {
        var track = p.Tracks.Single(t => t.Clips.Any(c => c.Id == id)); var c = track.Clips.Single(c => c.Id == id);
        if (track.Kind != "audio" || track.Locked) throw new InvalidOperationException("VideoEditor.SelectionLocked");
        var span = c.Duration * c.Speed; var speed = span / duration;
        if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration < 1 / p.Fps || speed < .0625 - 1e-9 || speed > 16 + 1e-9) throw new InvalidDataException("VideoEditor.SourceRange");
        var ratio = duration / c.Duration; c.Start = start; c.Duration = duration; c.Speed = Math.Clamp(speed, .0625, 16);
        c.FadeIn *= ratio; c.FadeOut *= ratio;
    }
}
