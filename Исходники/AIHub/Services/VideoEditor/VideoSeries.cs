using System.Globalization;

namespace AIHub.Services.VideoEditor;

[Flags]
public enum VideoSeriesLayers { None = 0, Video = 1, OriginalAudio = 2, Narration = 4, Subtitles = 8, OtherAudio = 16, All = 31 }

internal static class VideoSeries
{
    internal static int Allocate(VideoProject p) => checked(p.NextSeriesNumber++);
    internal static string Suffix(IEnumerable<int> series) => "_" + string.Join('+', series.Order().Select(n => n.ToString("D4", CultureInfo.InvariantCulture)));
    internal static VideoSeriesLayers Layer(VideoProject p, VideoTrack t, VideoClip c)
    {
        if (t.Kind != "audio") return VideoSeriesLayers.Video;
        if (c.NarrationId is not null || c.PlannedDuration is not null) return VideoSeriesLayers.Narration;
        if (c.AssetId is {} id && p.Asset(id).Kind == "video") return VideoSeriesLayers.OriginalAudio;
        return p.Narration.Any(n => n.AudioAssetId is not null && n.AudioAssetId == c.AssetId) ? VideoSeriesLayers.Narration : VideoSeriesLayers.OtherAudio;
    }
    internal static string Label(VideoProject p, VideoTrack track, VideoClip clip, Func<string, string> text)
    {
        var layer = Layer(p, track, clip);
        var index = p.Tracks.Where(t => t == track || t.Clips.Any(c => Layer(p, t, c) == layer)).TakeWhile(t => t != track).Count() + 1;
        return text("Series" + layer) + index + Suffix(clip.SeriesNumbers);
    }
    internal static string Label(VideoProject p, NarrationSegment n, Func<string, string> text)
    {
        var keys = p.Narration.Select(s => (s.SubtitleTrackId, s.Language)).Distinct().ToArray();
        return text("SeriesSubtitles") + (Array.IndexOf(keys, (n.SubtitleTrackId, n.Language)) + 1) + Suffix(n.SeriesNumbers);
    }
    internal static void Ensure(VideoProject p)
    {
        var all = p.Tracks.SelectMany(t => t.Clips).ToArray();
        var maximum = all.SelectMany(c => c.SeriesNumbers).Concat(p.Narration.SelectMany(n => n.SeriesNumbers)).DefaultIfEmpty(0).Max();
        p.NextSeriesNumber = Math.Max(p.NextSeriesNumber, checked(maximum + 1));
        if (all.All(c => c.SeriesNumbers.Count > 0) && p.Narration.All(n => n.SeriesNumbers.Count > 0)) return;
        // Existing explicit links take priority; time is only used to recover missing legacy associations.
        foreach (var c in p.Tracks.Where(t => t.Kind == "video").SelectMany(t => t.Clips).OrderBy(c => c.Start)) {
            if (c.SeriesNumbers.Count == 0) c.SeriesNumbers = c.LinkId is {} link && all.FirstOrDefault(a => a.LinkId == link && a.SeriesNumbers.Count > 0) is {} sibling ? [.. sibling.SeriesNumbers] : [Allocate(p)];
        }
        foreach (var n in p.Narration.Where(n => n.SeriesNumbers.Count == 0).OrderBy(n => n.Start)) {
            var voice = all.FirstOrDefault(c => c.NarrationId == n.Id && c.SeriesNumbers.Count > 0);
            var source = p.Tracks.Where(t => t.Kind == "video").SelectMany(t => t.Clips)
                .Where(c => c.AssetId is {} id && p.Asset(id).Kind == "video" && c.Start < n.End - .001 && c.Start + c.Duration > n.Start + .001)
                .OrderByDescending(c => Math.Min(c.Start + c.Duration, n.End) - Math.Max(c.Start, n.Start)).FirstOrDefault();
            n.SeriesNumbers = voice is not null ? [.. voice.SeriesNumbers] : source is not null ? [.. source.SeriesNumbers] : [Allocate(p)];
        }
        foreach (var c in all.Where(c => c.SeriesNumbers.Count == 0)) {
            var texts = p.Narration.Where(n => c.NarrationId == n.Id || c.NarrationId is null && c.AssetId is not null && n.AudioAssetId == c.AssetId).ToArray();
            var pair = c.LinkId is {} link ? all.FirstOrDefault(a => a.LinkId == link && a.SeriesNumbers.Count > 0) : null;
            c.SeriesNumbers = texts.Length > 0 ? texts.SelectMany(n => n.SeriesNumbers).Distinct().Order().ToList()
                : pair is not null ? [.. pair.SeriesNumbers] : [Allocate(p)];
        }
    }
    internal static VideoItemId[] Expand(VideoProject p, IReadOnlyCollection<VideoItemId> roots)
    {
        Ensure(p); var result = roots.ToHashSet();
        var clips = p.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).ToArray();
        var numbers = clips.Where(x => result.Contains(new(x.Clip.Id))).SelectMany(x => x.Clip.SeriesNumbers)
            .Concat(p.Narration.Where(n => result.Contains(new(n.Id, true))).SelectMany(n => n.SeriesNumbers)).ToHashSet();
        bool changed;
        do {
            changed = false;
            foreach (var (track, c) in clips.Where(x => (p.SeriesLayers & Layer(p, x.Track, x.Clip)) != 0))
                if (c.SeriesNumbers.Any(numbers.Contains)) { result.Add(new(c.Id)); foreach (var number in c.SeriesNumbers) changed |= numbers.Add(number); }
        } while (changed);
        if (p.SeriesLayers.HasFlag(VideoSeriesLayers.Subtitles)) foreach (var n in p.Narration.Where(n => n.SeriesNumbers.Any(numbers.Contains))) result.Add(new(n.Id, true));
        return result.ToArray();
    }
    internal static List<int> SplitNumbers(VideoProject p, IEnumerable<int> original, Dictionary<int, int> mapping)
        => original.Select(n => { if (!mapping.TryGetValue(n, out var next)) mapping[n] = next = Allocate(p); return next; }).ToList();
}
