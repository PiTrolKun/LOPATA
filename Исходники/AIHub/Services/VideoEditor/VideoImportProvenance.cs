using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

/// <summary>Preserve embedded LOPATA model history; never infer it from current settings.</summary>
internal static class VideoImportProvenance
{
    private const string ConverterMetadataLine = "LOPATA_CONVERTER_METADATA=";
    private sealed record ConverterTags(string Video, string Conversion);

    internal static OutputModelUse[] Models(JsonElement probe) => Read(probe)?.Models ?? [];

    internal static OutputProvenance? Read(JsonElement probe)
    {
        if (!probe.TryGetProperty("format", out var format)
            || ReadTag(format, "LOPATA_VIDEO") is not { Length: <= 48000 } text) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.String) text = document.RootElement.GetString()!;
            var facts = JsonSerializer.Deserialize<OutputProvenance>(text);
            if (facts is not { Program: "LOPATA", Schema: 1, Models.Length: <= 512 } || facts.Models.Any(m => m is null)) return null;
            _ = facts.Json(); return facts;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return null; }
    }

    internal static string? ReadTag(JsonElement format, string key)
    {
        if (DirectTag(format, key) is { } direct) return direct;
        if (key is not ("LOPATA_VIDEO" or "LOPATA_CONVERSION")) return null;
        var (_, tags) = ReadComment(DirectTag(format, "comment"));
        return key == "LOPATA_VIDEO" ? tags?.Video : tags?.Conversion;
    }

    internal static string? UserComment(JsonElement format) => ReadComment(DirectTag(format, "comment")).Comment;

    // The MP4 muxer's iTunes metadata stores cover art and the standard comment field.
    internal static string ConversionComment(string? comment, string provenance, string conversion) =>
        (string.IsNullOrEmpty(comment) ? "" : comment + "\n") + ConverterMetadataLine
        + JsonSerializer.Serialize(new ConverterTags(provenance, conversion));

    private static string? DirectTag(JsonElement format, string key)
    {
        if (!format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;
        var value = tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static (string? Comment, ConverterTags? Tags) ReadComment(string? comment)
    {
        if (comment is null) return (null, null);
        var marker = comment.LastIndexOf("\n" + ConverterMetadataLine, StringComparison.Ordinal);
        if (marker >= 0) marker++;
        else if (comment.StartsWith(ConverterMetadataLine, StringComparison.Ordinal)) marker = 0;
        if (marker < 0) return (comment, null);
        var value = comment.AsSpan(marker + ConverterMetadataLine.Length);
        if (value.Length > 128_000) return (comment, null);
        try
        {
            if (JsonSerializer.Deserialize<ConverterTags>(value.ToString()) is { Video.Length: > 0, Conversion.Length: > 0 } tags)
                return (marker == 0 ? "" : comment[..(marker - 1)], tags);
        }
        catch (JsonException) { }
        return (comment, null);
    }
}
