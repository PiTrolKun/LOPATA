namespace AIHub.Services.VideoEditor;

internal static class VideoEventCuts
{
    internal static void Apply(VideoProject p, string clipId, IReadOnlyList<NarrationSegment> sourceEvents, bool addNarration = true)
    {
        var track = p.Tracks.Single(t => t.Clips.Any(c => c.Id == clipId)); var original = track.Clips.Single(c => c.Id == clipId);
        if (track.Kind != "video" || original.Freeze) return;
        if (track.Locked || original.LinkId is {} link && p.Tracks.Any(t => t.Locked && t.Clips.Any(c => c.LinkId == link))) throw new InvalidOperationException("VideoEditor.LinkedLocked");
        var start = original.Start; var end = start + original.Duration; var sourceIn = original.SourceIn; var speed = original.Speed;
        var points = sourceEvents.Select(e => start + (e.Start - sourceIn) / speed)
            .Select(at => Math.Round(at * p.Fps) / p.Fps).Where(at => at > start + .01 && at < end - .01).Distinct().Order().ToArray();
        var tail = original;
        foreach (var point in points)
        {
            var before = track.Clips.Select(c => c.Id).ToHashSet();
            VideoEditing.Split(p, tail.Id, point);
            tail = track.Clips.Single(c => !before.Contains(c.Id));
        }
        bool Overlaps(NarrationSegment n, double a, double b) => n.End > a + .001 && n.Start < b - .001;
        foreach (var n in p.Narration.Where(n => n.SeriesNumbers.Intersect(original.SeriesNumbers).Any() && Overlaps(n, start, end))) {
            var numbers = track.Clips.Where(c => c.AssetId == original.AssetId && c.Start < end && c.Start + c.Duration > start && c.Start < n.End - .001 && c.Start + c.Duration > n.Start + .001).SelectMany(c => c.SeriesNumbers);
            n.SeriesNumbers = n.SeriesNumbers.Except(original.SeriesNumbers).Concat(numbers).Distinct().Order().ToList();
        }
        if (!addNarration) { VideoSeries.Ensure(p); return; }
        bool Protected(NarrationSegment n) => !string.IsNullOrWhiteSpace(n.Text) || n.AudioAssetId is not null || n.Models.Length > 0
            || n.Provider != p.DefaultVoice || n.Rate != p.DefaultRate || n.Language != p.DefaultLanguage || n.VoiceOptions != p.DefaultVoiceOptions
            || n.SubtitleTrackId != "narration" || n.SubtitleDefault || n.SubtitleForced;
        p.Narration.RemoveAll(n => Overlaps(n, start, end) && !Protected(n));
        var boundaries = new[] { start }.Concat(points).Append(end).ToArray();
        foreach (var (a, b) in boundaries.Zip(boundaries.Skip(1)))
            if (!p.Narration.Any(n => Overlaps(n, a, b))) p.Narration.Add(new() { Start = a, End = b, Provider = p.DefaultVoice, Language = p.DefaultLanguage, Rate = p.DefaultRate, VoiceOptions = p.DefaultVoiceOptions });
        VideoSeries.Ensure(p);
    }
}
