using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Lopata.Collection;

public sealed record CollectionOrigin(string Scenario, bool RequiresContentValidation = false);

/// <summary>Reads only format metadata; names, titles and full-text matches never establish origin.</summary>
public static class CollectionOriginReader
{
    internal const int MetadataLimit = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static CollectionOrigin? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || !CollectionFilePolicy.CanRead(file)) return null;
            if (CollectionAlternateOrigin.Read(path) is { } alternate) return alternate;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension switch
            {
                ".png" => Png(input),
                ".jpg" or ".jpeg" => Jpeg(input),
                ".webp" => WebP(input),
                ".gif" => CollectionMediaOrigin.Gif(input),
                ".mkv" or ".webm" => CollectionMediaOrigin.Matroska(input),
                ".mid" or ".midi" => CollectionMidiOrigin.Read(input),
                ".mp3" or ".flac" or ".opus" or ".ogg" or ".wav" => CollectionAudioOrigin.Read(input),
                ".mp4" or ".m4a" or ".mov" => CollectionVideoOrigin.Read(input),
                ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" or ".epub" or ".cbz"
                    or ".lopatadoxai" or ".lopatavideo" or ".lopataaivid" or ".lopatasvg" or ".zip" => Zip(input),
                ".svg" or ".fb2" => Xml(input),
                ".txt" or ".md" or ".html" or ".htm" or ".rtf" or ".csv" => CollectionTextOrigin.Read(input, extension),
                ".json" when input.Length <= MetadataLimit => ProjectJson(ReadUtf8(input, MetadataLimit)),
                ".pdf" => CollectionTextOrigin.Pdf(input),
                _ => null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or XmlException or DecoderFallbackException or ArgumentException or OverflowException
            or NotSupportedException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static CollectionOrigin? ProjectJson(string text) => Json(text) ?? CollectionMidiOrigin.Project(text);

    internal static CollectionOrigin? Json(string text)
    {
        text = text.TrimStart('\uFEFF');
        if (text.Length is 0 or > MetadataLimit) return null;
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Property(root, "Project", out var project) && project.ValueKind == JsonValueKind.Object) root = project;
        if (Property(root, "Provenance", out var provenance)) root = provenance;
        if (root.ValueKind != JsonValueKind.Object || !Property(root, "Program", out var program)
            || program.ValueKind != JsonValueKind.String || program.GetString() != "LOPATA") return null;
        if (Property(root, "Schema", out var schema) && (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number is < 1 or > 32)) return null;
        var scenario = Property(root, "Scenario", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        return new(scenario.Length <= 256 && !scenario.Any(char.IsControl) ? scenario : "");
    }

    private static bool Property(JsonElement value, string name, out JsonElement field) =>
        value.TryGetProperty(name, out field) || value.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out field);

    internal static CollectionOrigin? Xml(Stream input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MetadataLimit,
            IgnoreComments = true, CloseInput = false
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            var origin = reader.LocalName == "Provenance" && reader.NamespaceURI == "https://lopata.app/ns/provenance/1.0/"
                || reader.LocalName == "metadata" && reader.GetAttribute("id") == "lopata-provenance"
                || reader.LocalName == "property" && reader.GetAttribute("name") == "LOPATA.Provenance"
                || reader.LocalName == "custom-info" && reader.GetAttribute("info-type") == "LOPATA.Provenance"
                || reader.LocalName == "user-defined" && reader.GetAttribute("name", "urn:oasis:names:tc:opendocument:xmlns:meta:1.0") == "LOPATA.Provenance";
            if (origin)
            {
                using var subtree = reader.ReadSubtree();
                var element = XElement.Load(subtree);
                if (Json(element.Value) is { } result) return result;
            }
            if (reader.LocalName == "meta" && (reader.GetAttribute("property") == "lopata:provenance"
                || reader.GetAttribute("name") == "LOPATA.Provenance"))
            {
                var content = reader.GetAttribute("content") ?? reader.ReadElementContentAsString();
                if (Json(content) is { } result) return result;
            }
        }
        return null;
    }

    private static CollectionOrigin? Zip(Stream input)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > 4096) return null;
        foreach (var name in new[] { "manifest.json", "project.json", "conversation.json", "LOPATA.Provenance.json", "docProps/custom.xml", "meta.xml" })
        {
            var entry = zip.GetEntry(name);
            if (entry is null || entry.Length > MetadataLimit) continue;
            using var stream = entry.Open();
            var result = name.EndsWith(".json", StringComparison.Ordinal) ? Json(ReadUtf8(stream, MetadataLimit)) : Xml(stream);
            if (result is not null) return result;
        }
        var container = zip.GetEntry("META-INF/container.xml");
        if (container is null || container.Length > 32 * 1024) return null;
        using var c = container.Open();
        using var xr = XmlReader.Create(c, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768 });
        var document = XDocument.Load(xr);
        var opf = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        if (string.IsNullOrEmpty(opf) || opf.StartsWith('/') || opf.Contains('\\') || opf.Split('/').Any(p => p is ".." or "." || p.Contains(':'))) return null;
        var package = zip.GetEntry(opf);
        if (package is null || package.Length > MetadataLimit) return null;
        using var content = package.Open();
        return Xml(content);
    }

    private static CollectionOrigin? Png(Stream input)
    {
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return null;
        Span<byte> header = stackalloc byte[8];
        for (var chunks = 0; chunks < 16384 && input.Position + 12 <= input.Length; chunks++)
        {
            input.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size > input.Length - input.Position - 4) return null;
            var end = input.Position + size + 4;
            if (size <= MetadataLimit && (header[4..].SequenceEqual("iTXt"u8) || header[4..].SequenceEqual("tEXt"u8)))
            {
                var bytes = new byte[size]; input.ReadExactly(bytes);
                var split = Array.IndexOf(bytes, (byte)0);
                if (split >= 0)
                {
                    var name = Encoding.Latin1.GetString(bytes, 0, split);
                    if (name is "LOPATA" or "XML:com.adobe.xmp")
                    {
                        var offset = split + 1;
                        if (header[4..].SequenceEqual("iTXt"u8))
                        {
                            if (offset + 2 > bytes.Length || bytes[offset] != 0) { input.Position = end; continue; }
                            offset += 2;
                            offset = Array.IndexOf(bytes, (byte)0, offset) + 1;
                            if (offset == 0) return null;
                            offset = Array.IndexOf(bytes, (byte)0, offset) + 1;
                            if (offset == 0) return null;
                        }
                        var value = Utf8.GetString(bytes, offset, bytes.Length - offset);
                        var result = name == "LOPATA" ? Json(value) : Xml(new MemoryStream(bytes, offset, bytes.Length - offset));
                        if (result is not null) return result;
                    }
                }
            }
            input.Position = end;
            if (header[4..].SequenceEqual("IEND"u8)) break;
        }
        return null;
    }

    private static CollectionOrigin? Jpeg(Stream input)
    {
        if (input.ReadByte() != 0xff || input.ReadByte() != 0xd8) return null;
        Span<byte> length = stackalloc byte[2];
        for (var count = 0; count < 4096 && input.Position < Math.Min(input.Length, 8 * MetadataLimit); count++)
        {
            if (input.ReadByte() != 0xff) return null;
            int type; do { type = input.ReadByte(); } while (type == 0xff);
            if (type is 0xd9 or 0xda or -1) break;
            if (type is >= 0xd0 and <= 0xd7 or 0x01) continue;
            input.ReadExactly(length); var size = BinaryPrimitives.ReadUInt16BigEndian(length) - 2;
            if (size < 0 || size > input.Length - input.Position) return null;
            if (type == 0xe1)
            {
                var bytes = new byte[size]; input.ReadExactly(bytes);
                ReadOnlySpan<byte> prefix = "http://ns.adobe.com/xap/1.0/\0"u8;
                if (bytes.AsSpan().StartsWith(prefix) && Xml(new MemoryStream(bytes, prefix.Length, size - prefix.Length)) is { } origin) return origin;
            }
            else input.Position += size;
        }
        return null;
    }

    private static CollectionOrigin? WebP(Stream input)
    {
        Span<byte> header = stackalloc byte[12]; input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8)) return null;
        Span<byte> block = stackalloc byte[8];
        for (var count = 0; count < 4096 && input.Position + 8 <= input.Length; count++)
        {
            input.ReadExactly(block); var size = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            if (size > input.Length - input.Position) return null;
            if (block[..4].SequenceEqual("XMP "u8) && size <= MetadataLimit)
            {
                var bytes = new byte[size]; input.ReadExactly(bytes); return Xml(new MemoryStream(bytes));
            }
            input.Position += size;
            if ((size & 1) != 0) input.Position++;
        }
        return null;
    }

    internal static string ReadUtf8(Stream input, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Metadata exceeds its limit.");
            output.Write(buffer, 0, count);
        }
        return Utf8.GetString(output.ToArray());
    }
}
