using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Index payloads must cover the exact source text; Python offsets count Unicode scalars.</summary>
public static class ImportRagArtifact
{
    public static void Validate(string vectors, ImportRagInput input, int expectedPoints, CancellationToken ct)
    {
        var sections = input.Sections.ToDictionary(s => (s.Source, s.Section));
        var offsets = sections.ToDictionary(p => p.Key, p => ScalarOffsets(p.Value.Text));
        var ranges = sections.ToDictionary(p => p.Key, _ => new List<(int Start, int End)>());
        var ids = new HashSet<string>();
        using var reader = File.OpenText(vectors);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(line);
            var point = json.RootElement; LiterarySourceIndex.ValidatePoint(point);
            if (!ids.Add(point.GetProperty("id").ToString())) throw new InvalidDataException("Duplicate index point.");
            var payload = point.GetProperty("payload");
            var key = (payload.GetProperty("source").GetString()!, payload.GetProperty("section").GetString()!);
            if (!sections.TryGetValue(key, out var section) || payload.GetProperty("kind").GetString() != section.Kind)
                throw new InvalidDataException("Foreign index fragment.");
            var scalar = payload.GetProperty("offset").GetInt32();
            var map = offsets[key];
            if (scalar < 0 || scalar >= map.Length) throw new InvalidDataException("Invalid index offset.");
            var start = map[scalar]; var text = payload.GetProperty("text").GetString()!;
            if (string.IsNullOrWhiteSpace(text) || (long)start + text.Length > section.Text.Length
                || !section.Text.AsSpan(start, text.Length).SequenceEqual(text))
                throw new InvalidDataException("Index fragment differs from source.");
            ranges[key].Add((start, start + text.Length));
        }
        if (ids.Count != expectedPoints || ids.Count == 0) throw new InvalidDataException("Index point count differs.");
        foreach (var (key, section) in sections)
        {
            ct.ThrowIfCancellationRequested();
            var end = 0;
            foreach (var range in ranges[key].OrderBy(r => r.Start))
            {
                if (range.Start > end && !string.IsNullOrWhiteSpace(section.Text[end..range.Start]))
                    throw new InvalidDataException("Index has an uncovered passage.");
                end = Math.Max(end, range.End);
            }
            if (!string.IsNullOrWhiteSpace(section.Text[end..])) throw new InvalidDataException("Index lost the end of a section.");
        }
    }

    private static int[] ScalarOffsets(string text)
    {
        var result = new List<int>(); var offset = 0;
        foreach (var rune in text.EnumerateRunes()) { result.Add(offset); offset += rune.Utf16SequenceLength; }
        result.Add(offset); return result.ToArray();
    }
}
