using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicLyricsRecognizedWord(string Text, double StartSeconds, double EndSeconds, double Probability);
public sealed record MusicLyricsAlignmentResult(double DurationSeconds, string Provider, string ProviderVersion,
    string State, MusicTimedLyricsLine[] Lines, string Diagnostics);

/// <summary>Maps actual ASR intervals to ordered source occurrences. Never interpolates missing words or gaps.</summary>
public static partial class MusicLyricsAlignmentMatcher
{
    private sealed record SourceWord(string Text, int Start, int Length, int Line);
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’\-][\p{L}\p{N}][\p{L}\p{M}\p{N}]*)*", RegexOptions.CultureInvariant)]
    internal static partial Regex Words();
    internal static string Key(string word) => word.Normalize(NormalizationForm.FormD)
        .Replace("\u0301", "").Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('ё', 'е');

    public static MusicLyricsRecognizedWord[] ReadWhisperJson(string json, double duration)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<MusicLyricsRecognizedWord>();
        foreach (var segment in document.RootElement.GetProperty("transcription").EnumerateArray())
        {
            if (!segment.TryGetProperty("tokens", out var tokens)) continue;
            var text = new StringBuilder();
            var pieces = new List<(int Start, int Length, double From, double To, double Probability)>();
            foreach (var token in tokens.EnumerateArray())
            {
                var value = token.GetProperty("text").GetString() ?? "";
                if (value.StartsWith("[_", StringComparison.Ordinal) || value.StartsWith("<|", StringComparison.Ordinal)) continue;
                var start = text.Length; text.Append(value);
                if (!token.TryGetProperty("offsets", out var offsets)) continue;
                var from = offsets.GetProperty("from").GetDouble() / 1000;
                var to = offsets.GetProperty("to").GetDouble() / 1000;
                var probability = token.TryGetProperty("p", out var p) ? p.GetDouble() : 0;
                if (double.IsFinite(from) && double.IsFinite(to) && from >= 0 && to >= from && to <= duration + .001 &&
                    double.IsFinite(probability) && probability is >= 0 and <= 1)
                    pieces.Add((start, value.Length, from, Math.Min(to, duration), probability));
            }
            foreach (Match word in Words().Matches(text.ToString()))
            {
                var matching = pieces.Where(p => p.Start < word.Index + word.Length && p.Start + p.Length > word.Index).ToArray();
                if (matching.Length == 0 || matching.Sum(p => Math.Min(p.Start + p.Length, word.Index + word.Length) - Math.Max(p.Start, word.Index)) < word.Length) continue;
                var from = matching.Min(p => p.From); var to = matching.Max(p => p.To);
                var probability = matching.Average(p => p.Probability);
                if (to <= from || to - from > 12 || probability < .25 || result.Count > 0 && from < result[^1].EndSeconds) continue;
                result.Add(new(word.Value, from, to, probability));
            }
        }
        if (result.Count > 10000) throw new InvalidDataException("Oversized recognized lyrics.");
        return result.ToArray();
    }

    public static MusicLyricsAlignmentResult Match(string lyrics, IReadOnlyList<MusicLyricsRecognizedWord> recognized,
        double duration, string provider, string version)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Invalid alignment duration.");
        for (var i = 0; i < recognized.Count; i++) {
            var word = recognized[i];
            if (string.IsNullOrWhiteSpace(word.Text) || !double.IsFinite(word.StartSeconds) || !double.IsFinite(word.EndSeconds) ||
                word.StartSeconds < 0 || word.EndSeconds <= word.StartSeconds || word.EndSeconds > duration + .001 ||
                !double.IsFinite(word.Probability) || word.Probability is < 0 or > 1 || i > 0 && word.StartSeconds < recognized[i - 1].EndSeconds)
                throw new InvalidDataException("Invalid recognized word interval.");
        }
        lyrics = MusicTimedLyrics.Normalize(lyrics);
        var source = new List<SourceWord>();
        var lines = new List<(int Start, int Length)>(); var position = 0;
        foreach (var raw in lyrics.Split('\n'))
        {
            var line = lines.Count; lines.Add((position, raw.Length));
            if (!(raw.Trim().StartsWith('[') && raw.Trim().EndsWith(']')))
                foreach (Match word in Words().Matches(raw)) source.Add(new(Key(word.Value), position + word.Index, word.Length, line));
            position += raw.Length + 1;
        }
        if (source.Count > 4000 || recognized.Count > 10000 || (long)(source.Count + 1) * (recognized.Count + 1) > 16_000_000)
            throw new InvalidDataException("Lyrics exceed the bounded alignment limit.");
        var heard = recognized.Select(w => Key(w.Text)).ToArray();
        var stride = heard.Length + 1; var matrix = new int[(source.Count + 1) * stride];
        for (var i = source.Count - 1; i >= 0; i--)
            for (var j = heard.Length - 1; j >= 0; j--)
                matrix[i * stride + j] = source[i].Text == heard[j] ? 1 + matrix[(i + 1) * stride + j + 1]
                    : Math.Max(matrix[(i + 1) * stride + j], matrix[i * stride + j + 1]);
        var mapping = new Dictionary<int, int>();
        for (int i = 0, j = 0; i < source.Count && j < heard.Length;)
        {
            if (source[i].Text == heard[j]) { mapping[i++] = j++; }
            else if (matrix[(i + 1) * stride + j] > matrix[i * stride + j + 1]) i++;
            else j++;
        }
        var output = new List<MusicTimedLyricsLine>();
        foreach (var group in source.Select((word, index) => (word, index)).GroupBy(x => x.word.Line))
        {
            var all = group.ToArray(); var matched = all.Where(x => mapping.ContainsKey(x.index)).ToArray();
            // Both boundaries and a clear phrase must be heard; isolated recurring words are not evidence of a sung line.
            if (matched.Length < Math.Min(3, all.Length) || matched.Length / (double)all.Length < .75 ||
                matched[0].index != all[0].index || matched[^1].index != all[^1].index) continue;
            var intervals = matched.Select(x => new MusicTimedLyricsWord(x.word.Start, x.word.Length,
                recognized[mapping[x.index]].StartSeconds, recognized[mapping[x.index]].EndSeconds)).ToArray();
            if (intervals.Where((w, i) => i > 0 && w.StartSeconds - intervals[i - 1].EndSeconds > 3).Any() ||
                output.Count > 0 && intervals[0].StartSeconds < output[^1].EndSeconds) continue;
            var line = lines[group.Key];
            output.Add(new(line.Start, line.Length, intervals[0].StartSeconds, intervals[^1].EndSeconds) {
                Words = intervals, Confidence = matched.Length / (double)all.Length * matched.Average(x => recognized[mapping[x.index]].Probability) });
        }
        var count = source.Select(s => s.Line).Distinct().Count();
        return new(duration, provider, version, output.Count == 0 ? "unavailable" : output.Count == count ? "ready" : "partial",
            output.ToArray(), $"sourceWords={source.Count}; recognizedWords={recognized.Count}; matchedWords={mapping.Count}; timedLines={output.Count}/{count}; no inferred intervals");
    }
}
