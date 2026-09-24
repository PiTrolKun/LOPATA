using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Separate readable output from content warnings. Missing fact fields remain reviewable.</summary>
public static class ImportJellyResponse
{
    public static LiteraryJellyFact[] Parse(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        var rows = json.RootElement;
        if (rows.ValueKind == JsonValueKind.Object && rows.TryGetProperty("facts", out var facts)) rows = facts;
        // Some executors return the fact array directly, including [] when there are no facts.
        if (rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unreadable fact list.");
        return rows.EnumerateArray().Select(row =>
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unreadable fact row.");
            string Field(string key) => !row.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null
                ? "" : field.ValueKind == JsonValueKind.String ? field.GetString()! : field.GetRawText();
            return new LiteraryJellyFact { Subject = Field("subject"), Relation = Field("relation"), Value = Field("value"),
                Kind = Field("kind"), Evidence = Field("evidence") };
        }).ToArray();
    }

    public static bool IsSeparator(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || trimmed.Length >= 3
            && trimmed[0] is '-' or '*' or '_'
            && trimmed.All(c => char.IsWhiteSpace(c) || c == trimmed[0]);
    }
}
