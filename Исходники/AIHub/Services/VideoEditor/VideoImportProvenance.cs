using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

/// <summary>Preserve embedded LOPATA model history; never infer it from current settings.</summary>
internal static class VideoImportProvenance
{
    internal static OutputModelUse[] Models(JsonElement probe)
    {
        if (!probe.TryGetProperty("format", out var format) || !format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return [];
        var tag = tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals("LOPATA_VIDEO", StringComparison.OrdinalIgnoreCase));
        if (tag.Value.ValueKind != JsonValueKind.String || tag.Value.GetString() is not { Length: <= 48000 } text) return [];
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.String) text = document.RootElement.GetString()!;
            var facts = JsonSerializer.Deserialize<OutputProvenance>(text);
            if (facts is not { Program: "LOPATA", Schema: 1, Models.Length: > 0 and <= 512 } || facts.Models.Any(m => m is null)) return [];
            _ = facts.Json(); return facts.Models;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return []; }
    }
}
