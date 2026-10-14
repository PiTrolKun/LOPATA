using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Recover whole heard phrases when BPE word clocks fail. Never invent word times or interpolate gaps.</summary>
public static class MusicLyricsPhraseMatcher
{
    private sealed record Phrase(string[] Words, string Compact, double From, double To, double Probability);
    private sealed record Source(int Start, int Length, string[] Words, string Compact);
    public static MusicLyricsAlignmentResult Match(string json, string lyrics, MusicLyricsAlignmentResult baseline)
    {
        lyrics = MusicTimedLyrics.Normalize(lyrics);
        var source = new List<Source>(); var offset = 0;
        foreach (var text in lyrics.Split('\n')) {
            if (!string.IsNullOrWhiteSpace(text) && !(text.Trim().StartsWith('[') && text.Trim().EndsWith(']'))) {
                var words = Keys(text);
                if (words.Length > 0) source.Add(new(offset, text.Length, words, string.Concat(words)));
            }
            offset += text.Length + 1;
        }
        using var document = JsonDocument.Parse(json);
        var heard = new List<Phrase>(); var lastEnd = 0d;
        foreach (var segment in document.RootElement.GetProperty("transcription").EnumerateArray()) {
            var text = segment.GetProperty("text").GetString() ?? "";
            if (!segment.TryGetProperty("offsets", out var times)) continue;
            var from = times.GetProperty("from").GetDouble() / 1000; var to = times.GetProperty("to").GetDouble() / 1000;
            if (!double.IsFinite(from) || !double.IsFinite(to) || from < lastEnd || to <= from ||
                to > baseline.DurationSeconds + .001 || to - from > 20) continue;
            var probabilities = segment.TryGetProperty("tokens", out var tokens) ? tokens.EnumerateArray()
                .Where(t => t.TryGetProperty("text", out var v) && !(v.GetString() ?? "").StartsWith("[_", StringComparison.Ordinal))
                .Where(t => t.TryGetProperty("p", out _)).Select(t => t.GetProperty("p").GetDouble()).ToArray() : [];
            if (probabilities.Length == 0 || probabilities.Any(p => !double.IsFinite(p) || p is < 0 or > 1)) continue;
            var probability = probabilities.Average(); var words = Keys(text);
            if (probability < .5 || words.Length == 0 || text.Length > 512) continue;
            heard.Add(new(words, string.Concat(words), from, Math.Min(to, baseline.DurationSeconds), probability)); lastEnd = to;
        }
        if ((long)(source.Count + 1) * (heard.Count + 1) > 1_000_000 || source.Any(s => s.Compact.Length > 512))
            return baseline with { Diagnostics = baseline.Diagnostics + "; phrase fallback skipped: bounded limit" };
        var stride = heard.Count + 1; var scores = new int[source.Count * heard.Count];
        var matrix = new int[(source.Count + 1) * stride];
        for (var i = source.Count - 1; i >= 0; i--)
            for (var j = heard.Count - 1; j >= 0; j--) {
                var score = Score(source[i], heard[j]); scores[i * heard.Count + j] = score;
                matrix[i * stride + j] = Math.Max(score + matrix[(i + 1) * stride + j + 1],
                    Math.Max(matrix[(i + 1) * stride + j], matrix[i * stride + j + 1]));
            }
        var output = baseline.Lines.ToList(); var added = 0;
        for (int i = 0, j = 0; i < source.Count && j < heard.Count;) {
            var score = scores[i * heard.Count + j];
            if (score > 0 && matrix[i * stride + j] == score + matrix[(i + 1) * stride + j + 1]) {
                var s = source[i++]; var h = heard[j++];
                if (output.Any(l => l.SourceStart == s.Start || l.StartSeconds < h.To && h.From < l.EndSeconds ||
                    l.SourceStart < s.Start && l.EndSeconds > h.From || l.SourceStart > s.Start && l.StartSeconds < h.To)) continue;
                output.Add(new(s.Start, s.Length, h.From, h.To) { Confidence = score / 1000d * h.Probability }); added++;
            }
            else if (matrix[(i + 1) * stride + j] > matrix[i * stride + j + 1]) i++;
            else j++;
        }
        output.Sort((a, b) => a.SourceStart.CompareTo(b.SourceStart));
        return baseline with { Lines = output.ToArray(), State = output.Count == 0 ? "unavailable" : output.Count == source.Count ? "ready" : "partial",
            Diagnostics = baseline.Diagnostics + $"; recoveredPhrases={added}; totalLines={output.Count}/{source.Count}; measured segment boundaries, no inferred word intervals" };
    }
    private static string[] Keys(string text) => MusicLyricsAlignmentMatcher.Words().Matches(text)
        .Select(m => MusicLyricsAlignmentMatcher.Key(m.Value)).ToArray();
    private static int Score(Source source, Phrase heard)
    {
        if (source.Words.SequenceEqual(heard.Words)) return 1000;
        // Multiple exact word anchors plus close whole text; short repeated fragments are insufficient.
        if (heard.Words.Length > source.Words.Length + 1) return 0;
        var anchors = Lcs(source.Words, heard.Words);
        if (anchors < Math.Min(3, source.Words.Length) || anchors / (double)Math.Max(source.Words.Length, heard.Words.Length) < .65) return 0;
        var similarity = 2d * Lcs(source.Compact.ToCharArray(), heard.Compact.ToCharArray()) / (source.Compact.Length + heard.Compact.Length);
        return similarity >= .82 ? (int)Math.Round(similarity * 1000) : 0;
    }
    private static int Lcs<T>(T[] a, T[] b)
    {
        var row = new int[b.Length + 1];
        foreach (var value in a) {
            var previous = 0;
            for (var j = 1; j <= b.Length; j++) {
                var saved = row[j]; row[j] = EqualityComparer<T>.Default.Equals(value, b[j - 1]) ? previous + 1 : Math.Max(row[j], row[j - 1]); previous = saved;
            }
        }
        return row[^1];
    }
}
