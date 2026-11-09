using System.Buffers.Binary;
using System.Text;

namespace Lopata.Collection;

internal static class CollectionAudioOrigin
{
    private const int Limit = CollectionOriginReader.MetadataLimit;

    public static CollectionOrigin? Read(Stream input)
    {
        Span<byte> magic = stackalloc byte[12];
        if (input.Read(magic) < 4) return null;
        input.Position = 0;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (magic[..3].SequenceEqual("ID3"u8)) ReadId3(input, tags);
        else if (magic[..4].SequenceEqual("fLaC"u8)) ReadFlac(input, tags);
        else if (magic[..4].SequenceEqual("OggS"u8)) ReadOgg(input, tags);
        else if (magic[..4].SequenceEqual("RIFF"u8) && magic[8..].SequenceEqual("WAVE"u8)) ReadWave(input, tags);
        foreach (var key in new[] { "LOPATA_PROVENANCE", "LOPATA.Provenance", "LOPATA_VIDEO" })
            if (tags.TryGetValue(key, out var value) && CollectionOriginReader.Json(value) is { } origin) return origin;
        // Old music exports used a job identity plus producer/version, not an arbitrary title.
        if (tags.TryGetValue("LOPATA_JOB", out var job) && job.Length is > 0 and < 256
            && tags.TryGetValue("LOPATA_VERSION", out var version) && version.Length is > 0 and < 100
            && tags.TryGetValue("LOPATA_MODEL", out var model) && model.Length is > 0 and < 512) return new("music/generate");
        return null;
    }

    private static int Synchsafe(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || value[..4].ToArray().Any(b => b > 127)) throw new InvalidDataException("Invalid ID3 size.");
        return (value[0] << 21) | (value[1] << 14) | (value[2] << 7) | value[3];
    }

    private static void ReadId3(Stream input, Dictionary<string, string> tags, long? containerEnd = null)
    {
        Span<byte> header = stackalloc byte[10]; input.ReadExactly(header);
        if (!header[..3].SequenceEqual("ID3"u8) || header[3] is not (3 or 4) || (header[5] & 0xc0) != 0) return;
        var size = Synchsafe(header[6..]);
        var end = input.Position + size;
        if (end > (containerEnd ?? input.Length)) return;
        Span<byte> frame = stackalloc byte[10];
        var bytesRead = 0;
        for (var count = 0; count < 10000 && input.Position + 10 <= end; count++)
        {
            input.ReadExactly(frame);
            if (frame[0] == 0) break;
            var type = Encoding.ASCII.GetString(frame[..4]);
            var length = header[3] == 4 ? Synchsafe(frame[4..8]) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame[4..8]));
            if (length < 1 || length > end - input.Position) return;
            var flags = frame[8] | frame[9];
            if (flags != 0 || type != "TXXX" || length > Limit || bytesRead + length > Limit)
            {
                input.Position += length;
                continue;
            }
            var body = new byte[length]; input.ReadExactly(body); bytesRead += length;
            var width = body[0] is 1 or 2 ? 2 : 1;
            var split = 1;
            while (split + width <= body.Length && (body[split] != 0 || width == 2 && body[split + 1] != 0)) split += width;
            if (split + width > body.Length) continue;
            var name = Decode(body[0], body.AsSpan(1, split - 1));
            if (name.StartsWith("LOPATA", StringComparison.OrdinalIgnoreCase))
                tags[name] = Decode(body[0], body.AsSpan(split + width)).TrimEnd('\0');
        }
    }

    private static string Decode(byte type, ReadOnlySpan<byte> value) => type switch
    {
        0 => Encoding.Latin1.GetString(value),
        1 => value.StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode.GetString(value[2..]) : Encoding.Unicode.GetString(value).TrimStart('\uFEFF'),
        2 => Encoding.BigEndianUnicode.GetString(value),
        3 => new UTF8Encoding(false, true).GetString(value),
        _ => throw new InvalidDataException("Invalid ID3 encoding.")
    };

    private static void ReadFlac(Stream input, Dictionary<string, string> tags)
    {
        input.Position = 4;
        Span<byte> header = stackalloc byte[4];
        for (var count = 0; count < 256 && input.Position + 4 <= input.Length; count++)
        {
            input.ReadExactly(header); var length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (length > input.Length - input.Position) return;
            if ((header[0] & 0x7f) == 4 && length <= Limit)
            {
                var bytes = new byte[length]; input.ReadExactly(bytes); ReadComments(bytes, tags); return;
            }
            input.Position += length;
            if ((header[0] & 0x80) != 0) return;
        }
    }

    private static void ReadComments(ReadOnlySpan<byte> bytes, Dictionary<string, string> tags)
    {
        if (bytes.Length < 8) return;
        var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (vendorLength > bytes.Length - 8) return;
        var offset = checked(4 + (int)vendorLength);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]); offset += 4;
        if (count > 10000) return;
        for (var index = 0U; index < count && offset + 4 <= bytes.Length; index++)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]); offset += 4;
            if (size > bytes.Length - offset) return;
            var text = new UTF8Encoding(false, true).GetString(bytes.Slice(offset, (int)size)); offset += (int)size;
            var split = text.IndexOf('=');
            if (split > 0 && text.AsSpan(0, split).StartsWith("LOPATA", StringComparison.OrdinalIgnoreCase)) tags[text[..split]] = text[(split + 1)..];
        }
    }

    private static void ReadOgg(Stream input, Dictionary<string, string> tags)
    {
        using var packet = new MemoryStream();
        var header = new byte[27]; uint? serial = null;
        for (var page = 0; page < 128 && input.Position + 27 <= input.Length && input.Position < Limit * 4; page++)
        {
            input.ReadExactly(header); if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8)) return;
            var thisSerial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
            serial ??= thisSerial;
            var lacing = new byte[header[26]]; input.ReadExactly(lacing);
            foreach (var length in lacing)
            {
                var data = new byte[length]; input.ReadExactly(data);
                if (thisSerial != serial) continue;
                if (packet.Length + length > Limit) return;
                packet.Write(data);
                if (length == 255) continue;
                var bytes = packet.ToArray();
                if (bytes.AsSpan().StartsWith("OpusTags"u8)) { ReadComments(bytes.AsSpan(8), tags); return; }
                if (bytes.Length >= 7 && bytes[0] == 3 && bytes.AsSpan(1, 6).SequenceEqual("vorbis"u8)) { ReadComments(bytes.AsSpan(7), tags); return; }
                packet.SetLength(0);
            }
        }
    }

    private static void ReadWave(Stream input, Dictionary<string, string> tags)
    {
        input.Position = 12; Span<byte> header = stackalloc byte[8];
        for (var count = 0; count < 4096 && input.Position + 8 <= input.Length; count++)
        {
            input.ReadExactly(header); var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (size > input.Length - input.Position) return;
            var end = input.Position + size + (size & 1);
            if (header[..4].SequenceEqual("id3 "u8) || header[..4].SequenceEqual("ID3 "u8))
                ReadId3(input, tags, input.Position + size);
            input.Position = end;
        }
    }
}
