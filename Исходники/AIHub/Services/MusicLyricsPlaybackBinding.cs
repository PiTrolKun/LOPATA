using AIHub.Models;

namespace AIHub.Services;

public readonly record struct MusicPlaybackTextRange(int Start, int Length);

/// <summary>Playback cannot adopt another project's text or edit the current snapshot.</summary>
public static class MusicLyricsPlaybackBinding
{
    /// <summary>The selected verified performance owns the clock; generation/history state cannot replace it.</summary>
    public static MusicPlaybackTextRange? TryGetRange(MusicTimedLyrics timing, MusicTrack track, string lyrics, double seconds)
    {
        if (track.IsExample || track.JobId != timing.JobId || track.Variant != timing.Variant ||
            MusicTimedLyrics.Normalize(lyrics) != timing.SourceLyrics || timing.FindLine(seconds) is not { } line)
            return null;
        var from = OriginalOffset(lyrics, line.SourceStart);
        var to = OriginalOffset(lyrics, line.SourceStart + line.SourceLength);
        return from >= 0 && to > from ? new(from, to - from) : null;
    }

    // Compatibility for callers that additionally require a displayed history provenance.
    public static MusicPlaybackTextRange? TryGetRange(MusicTimedLyrics timing, MusicTrack track,
        MusicProject project, string? displayedLyricsJobId, string lyrics, double seconds)
    {
        if (track.IsExample || track.JobId != timing.JobId || track.Variant != timing.Variant ||
            displayedLyricsJobId is null || timing.ProjectId != project.Id ||
            !project.Steps.Any(step => step.JobId == timing.JobId && step.Number == timing.ProjectStep &&
                MusicTimedLyrics.Normalize(step.Snapshot.Lyrics) == timing.SourceLyrics) ||
            !project.Steps.Any(step => step.JobId == displayedLyricsJobId &&
                MusicTimedLyrics.Normalize(step.Snapshot.Lyrics) == timing.SourceLyrics) ||
            MusicTimedLyrics.Normalize(lyrics) != timing.SourceLyrics)
            return null;
        return TryGetRange(timing, track, lyrics, seconds);
    }

    private static int OriginalOffset(string text, int canonicalOffset)
    {
        var original = 0; var canonical = 0;
        while (original < text.Length && canonical < canonicalOffset)
        {
            if (text[original] == '\r' && original + 1 < text.Length && text[original + 1] == '\n') original++;
            original++; canonical++;
        }
        return canonical == canonicalOffset ? original : -1;
    }
}
