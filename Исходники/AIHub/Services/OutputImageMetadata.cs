using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace AIHub.Services;

/// <summary>Tags newly encoded temporary images without decoding/recompressing their pixels.</summary>
public static class OutputImageMetadata
{
    public static void Write(string path, string format, int width, int height, OutputProvenance facts)
    {
        if (format == "png")
        {
            PngMetadataWriter.Write(path, new Dictionary<string, string> { ["Software"] = "LOPATA", ["LOPATA"] = facts.Json() });
            return;
        }
        WriteXmp(path, format, width, height, facts.Xmp());
    }

    internal static void WriteXmp(string path, string format, int width, int height, string metadata, CancellationToken token = default)
    {
        var xmp = Encoding.UTF8.GetBytes(metadata);
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".metadata";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (format is "jpeg" or "jpg") WriteJpeg(input, output, xmp);
                else if (format == "webp") WriteWebp(input, output, width, height, xmp);
                else throw new InvalidDataException("Unsupported image format.");
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteJpeg(Stream input, Stream output, byte[] xmp)
    {
        if (input.ReadByte() != 255 || input.ReadByte() != 216) throw new InvalidDataException("Invalid JPEG.");
        output.Write([255, 216]);
        var prefix = "http://ns.adobe.com/xap/1.0/\0"u8;
        if (xmp.Length <= ushort.MaxValue - prefix.Length - 2)
            WriteApp1(output, prefix, xmp);
        else
        {
            // Adobe Extended XMP uses this digest as the identifier for the exact extended packet.
            var id = Convert.ToHexString(MD5.HashData(xmp));
            XNamespace x = "adobe:ns:meta/";
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            XNamespace note = "http://ns.adobe.com/xmp/note/";
            var link = new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", rdf),
                    new XElement(rdf + "Description", new XAttribute(rdf + "about", ""),
                        new XAttribute(XNamespace.Xmlns + "xmpNote", note), new XAttribute(note + "HasExtendedXMP", id))));
            WriteApp1(output, prefix, Encoding.UTF8.GetBytes(link.ToString(SaveOptions.DisableFormatting)));

            var extension = "http://ns.adobe.com/xmp/extension/\0"u8;
            Span<byte> header = stackalloc byte[40];
            Encoding.ASCII.GetBytes(id.AsSpan(), header[..32]);
            BinaryPrimitives.WriteUInt32BigEndian(header[32..36], (uint)xmp.Length);
            var maxChunk = ushort.MaxValue - extension.Length - header.Length - 2;
            for (var offset = 0; offset < xmp.Length;)
            {
                var count = Math.Min(maxChunk, xmp.Length - offset);
                BinaryPrimitives.WriteUInt32BigEndian(header[36..], (uint)offset);
                WriteApp1(output, extension, xmp.AsSpan(offset, count), header);
                offset += count;
            }
        }
        input.CopyTo(output);
    }

    private static void WriteApp1(Stream output, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> header = default)
    {
        var size = checked((ushort)(prefix.Length + header.Length + payload.Length + 2));
        output.Write([255, 225, (byte)(size >> 8), (byte)size]);
        output.Write(prefix);
        output.Write(header);
        output.Write(payload);
    }

    private static void WriteWebp(Stream input, Stream output, int width, int height, byte[] xmp)
    {
        Span<byte> header = stackalloc byte[12]; input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != input.Length - 8) throw new InvalidDataException("Invalid WebP.");
        input.ReadExactly(header[..8]);
        var extended = header[..4].SequenceEqual("VP8X"u8);
        var lossless = header[..4].SequenceEqual("VP8L"u8);
        if (!extended && !lossless && !header[..4].SequenceEqual("VP8 "u8)) throw new InvalidDataException("Unexpected new WebP layout.");
        output.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(input.Length - 8 + (extended ? 0 : 18) + 8 + xmp.Length + (xmp.Length & 1))));
        output.Write(size); output.Write("WEBP"u8);
        if (extended)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 10) throw new InvalidDataException("Invalid VP8X.");
            output.Write(header[..8]); input.ReadExactly(header[..10]); header[0] |= 4; output.Write(header[..10]);
        }
        else
        {
            if (width is < 1 or > 16777216 || height is < 1 or > 16777216) throw new InvalidDataException("Invalid WebP dimensions.");
            // VP8L's fifth payload byte carries the alpha-used flag (bit 4).
            var alpha = false;
            if (lossless) { var start = input.Position; input.ReadExactly(header[8..]); var fifth = input.ReadByte(); if (fifth < 0) throw new EndOfStreamException(); alpha = (fifth & 16) != 0; input.Position = start; }
            output.Write("VP8X"u8); output.Write(new byte[] { 10, 0, 0, 0, (byte)(4 | (alpha ? 16 : 0)), 0, 0, 0 });
            output.Write([(byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)]);
            output.Write(header[..8]);
        }
        input.CopyTo(output); output.Write("XMP "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)xmp.Length); output.Write(size); output.Write(xmp);
        if ((xmp.Length & 1) != 0) output.WriteByte(0);
    }
}
