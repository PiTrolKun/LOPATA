using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal static class FileMetadataPolicy
{
    internal static readonly Dictionary<string,string> Png = new()
    { ["title"]="Title", ["author"]="Author", ["comment"]="Description", ["keywords"]="Keywords", ["copyright"]="Copyright" };
    internal static readonly Dictionary<string,string> Xmp = new()
    { ["title"]="XMP-dc:Title", ["author"]="XMP-dc:Creator", ["comment"]="XMP-dc:Description",
      ["keywords"]="XMP-dc:Subject", ["copyright"]="XMP-dc:Rights" };
    internal static bool Protected(string name) => name.Contains("LOPATA", StringComparison.OrdinalIgnoreCase)
        || name.Split(':').Last().Equals("parameters", StringComparison.OrdinalIgnoreCase)
        || new[] { "Software", "CreatorTool", "SoftwareVersion", "History", "Seed", "Prompt" }
            .Contains(name.Split(':').Last(), StringComparer.OrdinalIgnoreCase);
    internal static bool Origin(string name) => Protected(name) || name.Contains("Encoder", StringComparison.OrdinalIgnoreCase);
    internal static bool Signature(IEnumerable<string> names)=>names.Any(n=>n.Contains("C2PA",StringComparison.OrdinalIgnoreCase)
        ||n.Contains("JUMBF",StringComparison.OrdinalIgnoreCase)||n.Contains("DigitalSignature",StringComparison.OrdinalIgnoreCase)
        ||n.Contains("SignatureValue",StringComparison.OrdinalIgnoreCase));
    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? ""
        : value.ValueKind == JsonValueKind.Array ? string.Join("; ", value.EnumerateArray().Select(Text))
        : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("x-default", out var text) ? Text(text) : value.ToString();
    internal static string RawValue(JsonElement raw, string key)
    {
        if (raw.TryGetProperty(key, out var value)) return Text(value);
        return raw.EnumerateObject().Where(p => p.Name.Split(':').First() == key.Split(':')[0]
            && p.Name.Split(':').Last().Equals(key.Split(':').Last(), StringComparison.OrdinalIgnoreCase)).Select(p => Text(p.Value)).FirstOrDefault() ?? "";
    }
    internal static void Validate(FileMetadataSnapshot snapshot, FileMetadataPatch patch)
    {
        if (snapshot.Error is not null || snapshot.Hash is null) throw new InvalidDataException("Metadata.ReadOnly");
        foreach (var pair in patch.Values)
        {
            if (pair.Value is null || pair.Value.Length > 64000 || pair.Value.Contains('\0')) throw new InvalidDataException("Metadata.InvalidValue");
            if (!snapshot.Fields.Any(f => f.Id == "edit:"+pair.Key && f.Editable)) throw new InvalidDataException("Metadata.Protected");
        }
        if (patch.Cover is { } cover)
        {
            if (!snapshot.CanReplaceCover) throw new InvalidDataException("Metadata.CoverReadOnly");
            _ = cover.SourceName;
            FileMetadataPreview.ValidateImage(cover.Bytes);
            using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(cover.Bytes));
            var mime = codec!.EncodedFormat == SkiaSharp.SKEncodedImageFormat.Jpeg ? "image/jpeg"
                : codec.EncodedFormat == SkiaSharp.SKEncodedImageFormat.Png ? "image/png" : "";
            if (mime.Length == 0 || mime != cover.Mime) throw new InvalidDataException("Metadata.InvalidImage");
        }
    }
}
