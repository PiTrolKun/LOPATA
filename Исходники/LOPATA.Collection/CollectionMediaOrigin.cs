using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

internal static class CollectionMediaOrigin
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static CollectionOrigin? JsonValue(string value)
    {
        try { return CollectionOriginReader.Json(value.TrimEnd('\0')); }
        catch (JsonException) { return null; }
    }

    internal static CollectionOrigin? Gif(Stream input)
    {
        Span<byte> header = stackalloc byte[13]; input.ReadExactly(header);
        if (!header[..6].SequenceEqual("GIF89a"u8) && !header[..6].SequenceEqual("GIF87a"u8)) return null;
        if ((header[10] & 128) != 0 && !Skip(input, 3L << ((header[10] & 7) + 1))) return null;
        Span<byte> image = stackalloc byte[9];
        for (var block = 0; block < 65536 && input.Position < input.Length; block++)
        {
            var type = input.ReadByte();
            if (type == 0x3b) return null;
            if (type == 0x21)
            {
                var extension = input.ReadByte();
                if (extension < 0) return null;
                if (extension == 0xfe)
                {
                    using var comment = new MemoryStream();
                    if (!SubBlocks(input, comment)) return null;
                    if (JsonValue(Utf8.GetString(comment.ToArray())) is { } origin) return origin;
                }
                else if (!SubBlocks(input, null)) return null;
            }
            else if (type == 0x2c)
            {
                input.ReadExactly(image);
                if ((image[8] & 128) != 0 && !Skip(input, 3L << ((image[8] & 7) + 1))) return null;
                if (input.ReadByte() < 0 || !SubBlocks(input, null)) return null;
            }
            else return null;
        }
        return null;
    }

    private static bool SubBlocks(Stream input, MemoryStream? output)
    {
        Span<byte> buffer = stackalloc byte[255];
        for (var block = 0; block < 65536; block++)
        {
            var size = input.ReadByte();
            if (size < 0 || size > input.Length - input.Position) return false;
            if (size == 0) return true;
            if (output is null) input.Position += size;
            else
            {
                if (output.Length + size > CollectionOriginReader.MetadataLimit) return false;
                input.ReadExactly(buffer[..size]); output.Write(buffer[..size]);
            }
        }
        return false;
    }

    private static bool Skip(Stream input, long size)
    { if (size > input.Length - input.Position) return false; input.Position += size; return true; }

    internal static CollectionOrigin? Matroska(Stream input)
    {
        if (!Element(input, input.Length, out var id, out var end, out var unknown) || id != 0x1a45dfa3
            || unknown || end - input.Position > 65536) return null;
        var header = new byte[checked((int)(end - input.Position))]; input.ReadExactly(header);
        var docType = false;
        using (var metadata = new MemoryStream(header, false))
            while (metadata.Position < metadata.Length)
            {
                if (!Element(metadata, metadata.Length, out var child, out var childEnd, out var indefinite) || indefinite) return null;
                if (child == 0x4282 && childEnd - metadata.Position <= 16)
                {
                    var bytes = new byte[(int)(childEnd - metadata.Position)]; metadata.ReadExactly(bytes);
                    docType = Encoding.ASCII.GetString(bytes) is "matroska" or "webm";
                }
                metadata.Position = childEnd;
            }
        if (!docType) return null;
        for (var top = 0; top < 64 && input.Position < input.Length; top++)
        {
            if (!Element(input, input.Length, out id, out end, out unknown)) return null;
            if (id == 0x18538067) return Segment(input, end);
            if (unknown) return null;
            input.Position = end;
        }
        return null;
    }

    private static CollectionOrigin? Segment(Stream input, long end)
    {
        var start = input.Position;
        for (var count = 0; count < 65536 && input.Position < end; count++)
        {
            if (!Element(input, end, out var id, out var next, out var unknown) || unknown) return null;
            if (id == 0x1254c367 && next - input.Position <= CollectionOriginReader.MetadataLimit)
            {
                var bytes = new byte[(int)(next - input.Position)]; input.ReadExactly(bytes);
                if (Tags(new MemoryStream(bytes, false), bytes.Length, 0) is { } origin) return origin;
            }
            else if (id == 0x114d9b74 && next - input.Position <= 65536)
            {
                var bytes = new byte[(int)(next - input.Position)]; input.ReadExactly(bytes);
                if (TagPosition(bytes) is { } relative && relative <= end - start)
                {
                    input.Position = start + relative;
                    if (Element(input, end, out var target, out var targetEnd, out var indefinite)
                        && target == 0x1254c367 && !indefinite && targetEnd - input.Position <= CollectionOriginReader.MetadataLimit)
                    {
                        var tags = new byte[(int)(targetEnd - input.Position)]; input.ReadExactly(tags);
                        if (Tags(new MemoryStream(tags, false), tags.Length, 0) is { } origin) return origin;
                    }
                }
            }
            input.Position = next;
        }
        return null;
    }

    private static long? TagPosition(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        for (var count = 0; count < 4096 && input.Position < input.Length; count++)
        {
            if (!Element(input, input.Length, out var id, out var end, out var unknown) || unknown) return null;
            if (id == 0x4dbb)
            {
                ulong position = 0, target = 0;
                while (input.Position < end)
                {
                    if (!Element(input, end, out var field, out var next, out unknown) || unknown) return null;
                    if (next - input.Position <= 8 && field is 0x53ab or 0x53ac)
                    {
                        ulong value = 0; while (input.Position < next) value = (value << 8) | (byte)input.ReadByte();
                        if (field == 0x53ab) target = value; else position = value;
                    }
                    input.Position = next;
                }
                if (target == 0x1254c367 && position <= long.MaxValue) return (long)position;
            }
            input.Position = end;
        }
        return null;
    }

    private static CollectionOrigin? Tags(Stream input, long end, int depth)
    {
        if (depth > 8) return null;
        string? name = null, value = null;
        for (var count = 0; count < 8192 && input.Position < end; count++)
        {
            if (!Element(input, end, out var id, out var next, out var unknown) || unknown) return null;
            if (id is 0x7373 or 0x67c8)
            {
                if (Tags(input, next, depth + 1) is { } origin) return origin;
            }
            else if (depth >= 2 && id is 0x45a3 or 0x4487)
            {
                var bytes = new byte[checked((int)(next - input.Position))]; input.ReadExactly(bytes);
                if (id == 0x45a3) name = Utf8.GetString(bytes).ToUpperInvariant(); else value = Utf8.GetString(bytes);
            }
            input.Position = next;
        }
        return name is "COMMENT" or "LOPATA_VIDEO" or "LOPATA_PROVENANCE" or "LOPATA.PROVENANCE"
            && value is not null ? JsonValue(value) : null;
    }

    private static bool Element(Stream input, long boundary, out uint id, out long end, out bool unknown)
    {
        id = 0; end = 0; unknown = false;
        var first = input.ReadByte(); if (first <= 0) return false;
        var length = 1; var marker = 128;
        while ((first & marker) == 0) { marker >>= 1; if (++length > 4) return false; }
        id = (uint)first;
        for (var index = 1; index < length; index++) { var b = input.ReadByte(); if (b < 0) return false; id = (id << 8) | (byte)b; }
        first = input.ReadByte(); if (first <= 0) return false;
        length = 1; marker = 128;
        while ((first & marker) == 0) { marker >>= 1; if (++length > 8) return false; }
        ulong size = (uint)(first & (marker - 1));
        for (var index = 1; index < length; index++) { var b = input.ReadByte(); if (b < 0) return false; size = (size << 8) | (byte)b; }
        unknown = size == (1UL << (7 * length)) - 1;
        if (input.Position > boundary || !unknown && size > (ulong)(boundary - input.Position)) return false;
        end = unknown ? boundary : input.Position + (long)size;
        return true;
    }
}
