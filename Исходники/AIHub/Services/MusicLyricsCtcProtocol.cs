using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Keep UTF-16 editor ranges in the host; the acoustic worker returns only row identities.</summary>
public static class MusicLyricsCtcProtocol
{
    public sealed record SourceRow(int Index, string Text, int SourceStart, int SourceLength);

    public static SourceRow[] SourceRows(string lyrics)
    {
        var normalized = MusicTimedLyrics.Normalize(lyrics);
        var rows = new List<SourceRow>(); var offset = 0;
        foreach (var raw in normalized.Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length > 0 && !(text.StartsWith('[') && text.EndsWith(']')))
                rows.Add(new(rows.Count, text, offset + raw.Length - raw.TrimStart().Length, text.Length));
            offset += raw.Length + 1;
        }
        if (rows.Count > 1000 || normalized.Length > 100_000)
            throw new InvalidDataException("Source lyrics exceed the phrase search limit.");
        return rows.ToArray();
    }

    public static MusicLyricsAlignmentResult Read(string json, string lyrics, double duration)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!double.IsFinite(duration) || duration is <= 0 or > 1800 ||
            root.GetProperty("schema").GetInt32() != 1 ||
            root.GetProperty("provider").GetString() != MusicLyricsAlignmentService.Provider ||
            root.GetProperty("version").GetString() != MusicLyricsAlignmentService.ProviderVersion ||
            !string.Equals(root.GetProperty("source_hash").GetString(), MusicTimedLyrics.HashLyrics(lyrics), StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(root.GetProperty("duration_seconds").GetDouble() - duration) > .001)
            throw new InvalidDataException("Acoustic result does not match the submitted audio/text protocol.");
        var source = SourceRows(lyrics); var lines = new List<MusicTimedLyricsLine>();
        var previousIndex = -1; var previousEnd = 0d;
        foreach (var item in root.GetProperty("lines").EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            var from = item.GetProperty("start").GetDouble(); var to = item.GetProperty("end").GetDouble();
            if (index <= previousIndex || index >= source.Length || !double.IsFinite(from) || !double.IsFinite(to) ||
                from < previousEnd || to <= from || to > duration + .001 || to - from > 30)
                throw new InvalidDataException("Invalid acoustic phrase range or ordering.");
            var row = source[index];
            lines.Add(new(row.SourceStart, row.SourceLength, from, Math.Min(to, duration)));
            previousIndex = index; previousEnd = to;
        }
        var state = root.GetProperty("state").GetString();
        var expected = lines.Count == 0 ? "unavailable" : lines.Count == source.Length ? "complete" : "partial";
        if (state != expected) throw new InvalidDataException("Acoustic completion state contradicts source coverage.");
        return new(duration, MusicLyricsAlignmentService.Provider, MusicLyricsAlignmentService.ProviderVersion,
            expected, lines.ToArray(), $"GigaAM v3 CTC; timedLines={lines.Count}/{source.Length}; whole-phrase acoustic search; no inferred intervals");
    }
}
