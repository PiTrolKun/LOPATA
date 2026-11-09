using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Lopata.Collection;

namespace AIHub.Services;

/// <summary>Projects only public model identities from the actually used input snapshot.</summary>
internal sealed class ImageUtilitySourceHistory
{
    private readonly HashSet<OutputModelUse> _models = [];
    private bool _seen, _incomplete;
    private int _bytes;

    internal static async Task<OutputProvenance> MergeAsync(OutputProvenance current, string copied, string xmp, string? original, CancellationToken token)
    {
        _ = current.Json();
        var history = new ImageUtilitySourceHistory();
        foreach (var model in current.Models) history.Add(model);
        try
        {
            using var file = File.OpenRead(copied); var signature = new byte[8];
            if (await file.ReadAsync(signature, token) == 8 && signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                foreach (var pair in PngTextMetadata.Read(copied))
                    if (pair.Key is "LOPATA" or "LOPATA.Provenance" or "LOPATA_PROVENANCE") history.Json(pair.Value);
            if (File.Exists(xmp) && new FileInfo(xmp).Length > 0)
            {
                if (new FileInfo(xmp).Length > 2 * 1024 * 1024) throw new InvalidDataException("Oversized source XMP.");
                using var reader = XmlReader.Create(xmp, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
                var xml = XDocument.Load(reader); XNamespace ns = "https://lopata.app/ns/provenance/1.0/";
                foreach (var element in xml.Descendants(ns + "Provenance")) history.Json(element.Value);
                foreach (var attribute in xml.Descendants().Attributes(ns + "Provenance")) history.Json(attribute.Value);
            }
        }
        catch (Exception error) when (MetadataError(error)) { history._incomplete = true; }
        await history.AlternateAsync(original, copied, token);
        return current with { Models = history._models.ToArray(), ModelHistory =
            (history._seen ? "source-metadata-and-current-operation; earlier-history-unknown" : current.ModelHistory)
            + (history._incomplete ? "; source-history-incomplete" : "") };
    }

    private async Task AlternateAsync(string? original, string copied, CancellationToken token)
    {
        if (original is null || !OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(original)) return;
        try
        {
            original = Path.GetFullPath(original);
            if (original.AsSpan(Path.GetPathRoot(original)?.Length ?? 0).Contains(':') || !CollectionFilePolicy.CanRead(new FileInfo(original))) return;
            using var input = new FileStream(original + ":" + CollectionAlternateOrigin.StreamName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length is <= 0 or > CollectionAlternateOrigin.MaximumBytes) throw new InvalidDataException("Invalid source provenance size.");
            var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
            using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out var size)
                || !root.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit)
                || !root.TryGetProperty("provenance", out var provenance)) throw new InvalidDataException("Invalid source provenance envelope.");
            using var snapshot = File.OpenRead(copied);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(snapshot, token));
            // Bind to pre-normalization copied bytes, even if the original changes during processing.
            if (snapshot.Length != size || !actual.Equals(digest, StringComparison.OrdinalIgnoreCase)) { _incomplete = true; return; }
            Json(provenance.GetRawText());
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (MetadataError(error) || error is NotSupportedException) { _incomplete = true; }
    }

    private void Json(string text)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(text) > CollectionAlternateOrigin.MaximumBytes) throw new InvalidDataException("Oversized source facts.");
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); var root = document.RootElement;
            if (Text(root, "Program") != "LOPATA" || !Field(root, "Schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException("Unsupported source facts.");
            _seen = true;
            if (Field(root, "Models", out var models))
            {
                if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 256) throw new InvalidDataException("Invalid source models.");
                foreach (var model in models.EnumerateArray())
                    if (Text(model, "Id") is { } id && Text(model, "Name") is { } name && Text(model, "Revision") is { } revision && Text(model, "Role") is { } role)
                        Add(new(id, name, revision, role));
                    else _incomplete = true;
            }
            // Existing generation PNGs record their pinned components outside Models[].
            if (Text(root, "ModelId") is { } legacy)
            {
                Add(new(legacy, Text(root, "Model") ?? legacy, Text(root, "ModelRevision") ?? "", "source image generation; source metadata"));
                if (Field(root, "Components", out var components) && components.ValueKind == JsonValueKind.Array)
                    foreach (var component in components.EnumerateArray().Take(256))
                        if (Text(component, "Id") is { } id && Text(component, "Revision") is { } revision)
                            Add(new(id, Text(component, "Repository") ?? id, revision, "source image generation component"));
            }
            if (Field(root, "AppliedLoras", out var loras))
            {
                if (loras.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid source LoRAs.");
                if (loras.GetArrayLength() > 256) _incomplete = true;
                foreach (var lora in loras.EnumerateArray().Take(256))
                    if (Text(lora, "Id") is { } id && Text(lora, "Repository") is { } name && Text(lora, "Revision") is { } revision)
                        Add(new(id, name, revision, "source image generation LoRA"));
                    else _incomplete = true;
            }
        }
        catch (Exception error) when (MetadataError(error)) { _incomplete = true; }
    }

    private void Add(OutputModelUse model)
    {
        if (_models.Contains(model)) return;
        if (string.IsNullOrWhiteSpace(model.Id) || new[] { model.Id, model.Name, model.Revision, model.Role }
            .Any(value => value is null || value.Length > 1024 || Path.IsPathRooted(value) || value.Any(char.IsControl)))
        { _incomplete = true; return; }
        var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(model));
        if (_models.Count >= 256 || _bytes + size > 40_000) { _incomplete = true; return; }
        _models.Add(model); _bytes += size;
    }
    private static bool Field(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.ValueKind == JsonValueKind.Object && (root.TryGetProperty(name, out value) || root.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out value));
    }
    private static string? Text(JsonElement root, string name) => Field(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool MetadataError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or XmlException
        or InvalidDataException or ArgumentException or DecoderFallbackException or InvalidOperationException or System.Security.SecurityException;
}
