using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Accepts textual observations, including lists and named sections, without losing their structure.</summary>
public static class ImageBatchAnalysisParser
{
    public static ImageBatchAnalysis Parse(string content)
    {
        if (content.Length > 1_048_576) throw new InvalidDataException("Batch analysis is too large.");
        int first = content.IndexOf('{'), last = content.LastIndexOf('}');
        if (first < 0 || last <= first) throw new InvalidDataException("Missing batch JSON.");
        using var document = JsonDocument.Parse(content[first..(last + 1)], new() { MaxDepth = 32 });
        var root = document.RootElement;
        return new(Read(root, "details"), Read(root, "summary"));
    }

    private static string Read(JsonElement root, string name)
    {
        var matches = root.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !HasText(matches[0].Value)) throw new InvalidDataException("Missing or empty batch " + name + ".");
        var value = matches[0].Value;
        // Preserve property names, ordering, numbers, nested values and uncertainty qualifiers.
        // A structured observation becomes JSON text, not an invented prose summary.
        return value.ValueKind == JsonValueKind.String ? value.GetString()!
            : JsonSerializer.Serialize(value, ImageBatchStore.Json);
    }

    private static bool HasText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.EnumerateArray().Any(HasText),
        JsonValueKind.Object => value.EnumerateObject().Any(p => HasText(p.Value)),
        _ => false
    };
}
