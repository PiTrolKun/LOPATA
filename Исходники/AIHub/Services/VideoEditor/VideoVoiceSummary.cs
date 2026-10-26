namespace AIHub.Services.VideoEditor;

internal sealed record VideoVoiceSummaryState(VideoSpeechProfile?[] Profiles, bool Recorded, bool Playback);

/// <summary>Keep planned choices separate from the actual audio's immutable synthesis profile.</summary>
internal static class VideoVoiceSummary
{
    internal static VideoSpeechProfile Planned(NarrationSegment segment) => new(segment.Provider, segment.Language, segment.Rate, segment.VoiceOptions);
    internal static VideoSpeechProfile? Recorded(VideoProject project, NarrationSegment segment) =>
        project.Assets.FirstOrDefault(a => a.Id == segment.AudioAssetId)?.NarratorProfile;

    internal static VideoVoiceSummaryState Resolve(VideoProject project, IReadOnlySet<string> selected, bool playing, double position)
    {
        if (playing)
        {
            var narrationAssets = project.Narration.Where(n => n.AudioAssetId is not null).Select(n => n.AudioAssetId).ToHashSet();
            var profiles = new List<VideoSpeechProfile?>();
            foreach (var clip in project.Tracks.Where(t => !t.Muted && !t.Hidden && t.Volume > 0 && t.Kind is "audio" or "video")
                .SelectMany(t => t.Clips).Where(c => c.Volume > 0 && position >= c.Start && position < c.Start + c.Duration))
            {
                var asset = project.Assets.FirstOrDefault(a => a.Id == clip.AssetId);
                if (asset is not null && (asset.NarratorProfile is not null || narrationAssets.Contains(asset.Id)
                    || asset.Models.Any(m => m.Role == "narration speech synthesis"))) profiles.Add(asset.NarratorProfile);
            }
            return new(profiles.Distinct().ToArray(), true, true);
        }
        var segments = project.Narration.Where(n => selected.Contains(n.Id)).ToArray();
        if (segments.Length == 0) return new([new(project.DefaultVoice, project.DefaultLanguage, project.DefaultRate, project.DefaultVoiceOptions)], false, false);
        if (segments.All(n => !n.Stale && n.AudioAssetId is not null && project.Assets.Any(a => a.Id == n.AudioAssetId)))
            return new(segments.Select(n => Recorded(project, n)).Distinct().ToArray(), true, false);
        return new(segments.Select(Planned).Distinct().Cast<VideoSpeechProfile?>().ToArray(), false, false);
    }
}
