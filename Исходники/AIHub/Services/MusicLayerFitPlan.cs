using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicLayerTimeAnchor(double Original, double Fresh);
public sealed record MusicLayerFitSummary(string Method, bool Approximate, int MatchedPhrases, int ExpectedPhrases,
    double OriginalSeconds, double FreshSeconds, double MaximumShiftSeconds, MusicLayerTimeAnchor[] Anchors)
{
    public string ProcessorVersion { get; init; } = MusicSongMetadata.AppVersion;
}

internal static class MusicLayerFitPlan
{
    internal static MusicLayerFitSummary Create(double originalSeconds, double freshSeconds, string originalLyrics, string freshLyrics,
        MusicLyricsAlignmentResult? original, MusicLyricsAlignmentResult? fresh)
    {
        var ratio = freshSeconds / originalSeconds;
        if (!double.IsFinite(ratio) || originalSeconds is <= 0 or > 360 || freshSeconds is <= 0 or > 360 || ratio is < .6 or > 1.65)
            throw new InvalidDataException("Music.Transform.FitTooDifferent");
        var rowsA = MusicLyricsCtcProtocol.SourceRows(originalLyrics); var rowsB = MusicLyricsCtcProtocol.SourceRows(freshLyrics);
        var anchors = new List<MusicLayerTimeAnchor> { new(0, 0) }; var matched = 0;
        if (original is not null && fresh is not null && rowsA.Length == rowsB.Length) {
            var a = ValidLines(original, rowsA, originalSeconds); var b = ValidLines(fresh, rowsB, freshSeconds);
            for (var i = 0; i < rowsA.Length; i++) {
                if (!a.TryGetValue(rowsA[i].SourceStart, out var x) || !b.TryGetValue(rowsB[i].SourceStart, out var y)) continue;
                var before = anchors.Count;
                Add(x.StartSeconds, y.StartSeconds); Add(x.EndSeconds, y.EndSeconds);
                if (anchors.Count > before) matched++;
            }
        }
        // Sparse acoustic matches cannot establish a dependable phrase map. Keep the result available,
        // with a clearly marked duration-only approximation instead of inventing missing phrase times.
        var minimum = Math.Min(3, rowsA.Length);
        if (minimum == 0 || matched < minimum || matched < rowsA.Length * .3) { anchors.RemoveRange(1, anchors.Count - 1); matched = 0; }
        anchors.Add(new(originalSeconds, freshSeconds));
        return new(matched > 0 ? "phrase-wsola-v1" : "duration-wsola-v1", true, matched, rowsA.Length,
            originalSeconds, freshSeconds, anchors.Max(p => Math.Abs(p.Original - p.Fresh)), anchors.ToArray());

        void Add(double target, double input)
        {
            var last = anchors[^1]; var dx = target - last.Original; var dy = input - last.Fresh;
            if (dx < .3 || dy < .3 || target >= originalSeconds - .3 || input >= freshSeconds - .3) return;
            // Allow an intro offset; subsequent voiced intervals must stay within a useful range.
            if (anchors.Count > 1 && dy / dx is < .5 or > 2) return;
            if (Math.Abs(target - input) > Math.Max(12, originalSeconds * .2)) return;
            anchors.Add(new(target, input));
        }
    }

    private static Dictionary<int, MusicTimedLyricsLine> ValidLines(MusicLyricsAlignmentResult result, MusicLyricsCtcProtocol.SourceRow[] rows, double duration)
    {
        var expected = rows.ToDictionary(r => r.SourceStart); var lines = new Dictionary<int, MusicTimedLyricsLine>();
        if (result.Lines is null || !double.IsFinite(result.DurationSeconds) || Math.Abs(result.DurationSeconds - duration) > .001)
            throw new InvalidDataException("Music.Transform.InvalidInput");
        var end = 0d;
        foreach (var line in result.Lines) {
            if (!expected.TryGetValue(line.SourceStart, out var row) || line.SourceLength != row.SourceLength
                || !double.IsFinite(line.StartSeconds) || !double.IsFinite(line.EndSeconds) || line.StartSeconds < end
                || line.EndSeconds <= line.StartSeconds || line.EndSeconds > duration + .001)
                throw new InvalidDataException("Music.Transform.InvalidInput");
            lines.Add(line.SourceStart, line); end = line.EndSeconds;
        }
        return lines;
    }
}
