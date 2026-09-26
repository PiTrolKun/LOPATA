using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AIHub.Services;

internal static class LiteraryMemorySearchSources
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static MemorySearchSource[] Build(ParagraphEvidence evidence)
    {
        var result = new List<MemorySearchSource>();
        string previousKey = "";
        foreach (var material in evidence.Materials)
        {
            var serialized = ParagraphJson.Encode(material.Data);
            var data = JsonNode.Parse(serialized);
            var text = data is JsonObject obj && obj["text"] is JsonValue value && value.TryGetValue<string>(out var content)
                ? content : serialized;
            var coordinate = text == serialized ? "serialized_material_utf16" : "source_text_utf16";
            var offset = coordinate == "source_text_utf16" ? Integer(data, "offset") : 0;
            var revision = coordinate == "source_text_utf16" ? String(data, "revision", material.Revision) : material.Revision;
            var number = String(data, "number", String(data, "Number", ""));
            var label = string.Join(" · ", new[] { number, String(data, "label", ""), String(data, "source", ""),
                String(data, "section", ""), String(data, "FileName", "") }.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
            if (label.Length == 0) label = material.Id.Split('@')[0];
            // Consecutive pages of the same registered text become one stream so that a later
            // extraction window can include both sides of the reader's 2,400-character boundary.
            var key = ParagraphJson.Encode(new { material.Kind, revision, number,
                file = String(data, "FileName", ""), source = String(data, "source", ""), section = String(data, "section", "") });
            if (coordinate == "source_text_utf16" && number.Length > 0 && previousKey == key && result.Count > 0
                && result[^1].Offset + result[^1].Text.Length == offset)
            {
                var previous = result[^1];
                result[^1] = previous with { Text = previous.Text + text, Materials = [.. previous.Materials, material.Id] };
            }
            else
                result.Add(new("s" + (result.Count + 1), material.Kind, revision, text, coordinate, offset, label, [material.Id]));
            previousKey = key;
        }
        return result.ToArray();
    }

    private static string String(JsonNode? node, string name, string fallback) =>
        node is JsonObject obj && obj[name] is JsonValue value ? value.ToString() : fallback;
    private static int Integer(JsonNode? node, string name) =>
        node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0 ? number : 0;

    public static (MemorySearchWindow[] Windows, MemorySearchCursor End) Slice(MemorySearchSource[] sources,
        MemorySearchCursor start, int characters)
    {
        if (characters < 1) throw new ArgumentOutOfRangeException(nameof(characters));
        var windows = new List<MemorySearchWindow>(); var current = start; var remaining = characters;
        while (current.Source < sources.Length && remaining > 0 && windows.Count < 64)
        {
            var source = sources[current.Source];
            if (current.Offset == source.Text.Length) { current = new(current.Source + 1, 0); continue; }
            var end = Math.Min(source.Text.Length, current.Offset + remaining);
            if (end < source.Text.Length && end > current.Offset && char.IsHighSurrogate(source.Text[end - 1])) end--;
            if (end == current.Offset)
            {
                if (windows.Count > 0) break;
                end = Math.Min(source.Text.Length, current.Offset + 2); // One supplementary Unicode character.
            }
            if (end < source.Text.Length && end - current.Offset > 256)
            {
                var line = source.Text.LastIndexOf('\n', end - 1, Math.Min(end - current.Offset, (end - current.Offset) / 3));
                if (line >= current.Offset) end = line + 1;
            }
            var overlap = Math.Min(160, characters / 4);
            var from = Math.Max(0, current.Offset - overlap);
            if (from > 0 && char.IsLowSurrogate(source.Text[from])) from--;
            windows.Add(new(source.Id, source.Kind, source.Label, source.Coordinate, source.Offset + from,
                source.Offset + current.Offset, source.Text[from..end]));
            remaining -= end - current.Offset;
            current = end == source.Text.Length ? new(current.Source + 1, 0) : new(current.Source, end);
            if (end < source.Text.Length) break;
        }
        // Empty data is genuinely zero characters, not a model-produced empty finding.
        while (current.Source < sources.Length && sources[current.Source].Text.Length == 0) current = new(current.Source + 1, 0);
        return (windows.ToArray(), current);
    }

    public static void Validate(MemorySearchPass pass, MemorySearchSource[] sources, MemorySearchCursor expected)
    {
        if (pass.Version != 1 || pass.Start != expected || pass.Windows is null || pass.Findings is null
            || pass.End.Source < pass.Start.Source || pass.End.Source > sources.Length || pass.End.Offset < 0
            || (pass.End.Source == pass.Start.Source && pass.End.Offset <= pass.Start.Offset)
            || (pass.End.Source < sources.Length && pass.End.Offset > sources[pass.End.Source].Text.Length)
            || (pass.End.Source == sources.Length && pass.End.Offset != 0))
            throw new InvalidDataException("Invalid memory search checkpoint coverage. Original files retained.");
        var cursor = expected;
        foreach (var window in pass.Windows)
        {
            while (cursor.Source < sources.Length && cursor.Offset == sources[cursor.Source].Text.Length) cursor = new(cursor.Source + 1, 0);
            if (cursor.Source >= sources.Length) throw new InvalidDataException("Unexpected checkpoint window.");
            var source = sources[cursor.Source];
            var start = window.Start - source.Offset; var end = start + window.Text.Length;
            if (window.Source != source.Id || window.Kind != source.Kind || window.Label != source.Label || window.Coordinate != source.Coordinate
                || window.CoveredStart != cursor.Offset + source.Offset || start < 0 || start > cursor.Offset
                || end <= cursor.Offset || end > source.Text.Length || source.Text[start..end] != window.Text)
                throw new InvalidDataException("Memory search checkpoint does not match the captured source.");
            cursor = end == source.Text.Length ? new(cursor.Source + 1, 0) : new(cursor.Source, end);
        }
        while (cursor.Source < sources.Length && sources[cursor.Source].Text.Length == 0) cursor = new(cursor.Source + 1, 0);
        if (cursor != pass.End) throw new InvalidDataException("Memory search checkpoint has a coverage gap.");
        foreach (var finding in pass.Findings)
        {
            var window = pass.Windows.SingleOrDefault(w => w.Source == finding.Source && finding.Start >= w.Start && finding.End <= w.Start + w.Text.Length);
            if (window is null || finding.Quote.Length == 0 || finding.End != finding.Start + finding.Quote.Length
                || window.Text.Substring(finding.Start - window.Start, finding.Quote.Length) != finding.Quote
                || string.IsNullOrWhiteSpace(finding.Text) || finding.Objects is null)
                throw new InvalidDataException("Memory search checkpoint contains an ungrounded finding.");
        }
    }
}
