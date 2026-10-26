namespace AIHub.Services.VideoEditor;

internal static class VideoSelectionMap
{
    internal static VideoItemId[] Expand(VideoProject p, IReadOnlyCollection<VideoItemId> roots)
    {
        var result = roots.ToHashSet(); if (roots.Count == 0) return [];
        var all = p.Tracks.SelectMany(t => t.Clips).ToArray(); var cues = VideoSubtitles.Cues(p).Where(n => n.End > n.Start).ToDictionary(n => n.Id);
        var rootClips = VideoSelectionEditing.Clips(p, roots, false);
        bool Overlap(VideoClip c, NarrationSegment n) => c.Start < n.End - .001 && c.Start + c.Duration > n.Start + .001;
        bool SourceVideo(VideoClip c) => c.AssetId is {} id && p.Asset(id).Kind == "video";
        var subtitles = roots.Where(i => i.Subtitle).Select(i => i.Id).ToHashSet();
        foreach (var c in rootClips) {
            result.Add(new(c.Id));
            if (c.NarrationId is {} n) subtitles.Add(n);
            else if (p.Narration.Any(n => n.AudioAssetId is not null && n.AudioAssetId == c.AssetId))
                subtitles.UnionWith(p.Narration.Where(n => n.AudioAssetId == c.AssetId).Select(n => n.Id));
            else if (SourceVideo(c)) subtitles.UnionWith(cues.Values.Where(n => Overlap(c, n)).Select(n => n.Id));
        }
        // A whole generated voice clip cannot be moved/deleted for only one of its cues.
        var sharedVoices = all.Where(c => c.NarrationId is null && c.AssetId is not null && p.Narration.Any(n => subtitles.Contains(n.Id) && n.AudioAssetId == c.AssetId)).Select(c => c.AssetId!).ToHashSet();
        subtitles.UnionWith(p.Narration.Where(n => n.AudioAssetId is {} id && sharedVoices.Contains(id)).Select(n => n.Id));
        foreach (var id in subtitles.Where(cues.ContainsKey)) {
            result.Add(new(id, true)); var n = p.Narration.Single(n => n.Id == id); var cue = cues[id];
            foreach (var c in p.Tracks.Where(t => t.Kind == "video" && !t.Hidden).SelectMany(t => t.Clips).Where(c => SourceVideo(c) && Overlap(c, cue))) result.Add(new(c.Id));
            foreach (var c in all.Where(c => c.NarrationId == id || n.AudioAssetId is not null && c.AssetId == n.AudioAssetId)) result.Add(new(c.Id));
        }
        foreach (var c in VideoSelectionEditing.Clips(p, result.ToArray(), false)) result.Add(new(c.Id));
        return result.ToArray();
    }
}
