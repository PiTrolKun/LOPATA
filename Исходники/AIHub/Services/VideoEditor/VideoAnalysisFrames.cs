using System.IO;

namespace AIHub.Services.VideoEditor;

internal readonly record struct VideoAnalysisFrame(string AssetId, double TimelineTime, double SourceTime);
internal static class VideoAnalysisFrames
{
    internal static VideoAnalysisFrame[] Plan(VideoProject p, NarrationSegment segment)
    {
        var clips = p.Tracks.Where(t => t.Kind == "video" && !t.Hidden).SelectMany(t => t.Clips.OrderBy(c => c.Start))
            .Where(c => c.AssetId is {} id && p.Asset(id).Kind == "video" && c.Start < segment.End && c.Start + c.Duration > segment.Start).ToArray();
        if (clips.Length == 0) throw new InvalidDataException("VideoEditor.NoVideoInFragment");
        var margin = Math.Min(.05, (segment.End - segment.Start) / 4);
        return new[] { segment.Start + margin, (segment.Start + segment.End) / 2, segment.End - margin }.Select(at => {
            var visible = clips.LastOrDefault(c => at >= c.Start && at < c.Start + c.Duration);
            if (visible is null) {
                visible = clips.MinBy(c => Math.Abs(at - Math.Clamp(at, Math.Max(segment.Start, c.Start), Math.Min(segment.End, c.Start + c.Duration))))!;
                var from = Math.Max(segment.Start, visible.Start); var to = Math.Min(segment.End, visible.Start + visible.Duration);
                var inset = Math.Min(.001, (to - from) / 4); at = Math.Clamp(at, from + inset, to - inset);
            }
            var a = p.Asset(visible.AssetId!); var source = visible.SourceIn + (visible.Freeze ? 0 : (at - visible.Start) * visible.Speed);
            return new VideoAnalysisFrame(a.Id, at, Math.Clamp(source, 0, Math.Max(0, a.Duration - .001)));
        }).Distinct().ToArray();
    }
}
