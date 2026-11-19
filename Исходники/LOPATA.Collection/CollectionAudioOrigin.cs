using System.Buffers.Binary;
using System.Text;

namespace Lopata.Collection;

internal static class CollectionAudioOrigin
{
    private const int Limit = CollectionOriginReader.MetadataLimit;

    public static CollectionOrigin? Read(Stream input)
    {
        var tags = ReadTags(input, includeMusic: false);
        foreach (var key in new[] { "LOPATA_PROVENANCE", "LOPATA.Provenance", "LOPATA_VIDEO" })
            if (tags.TryGetValue(key, out var value) && CollectionOriginReader.Json(value) is { } origin) return origin;
        // Old music exports used a job identity plus producer/version, not an arbitrary title.
        if (tags.TryGetValue("LOPATA_JOB", out var job) && job.Length is > 0 and < 256
            && tags.TryGetValue("LOPATA_VERSION", out var version) && version.Length is > 0 and < 100
            && tags.TryGetValue("LOPATA_MODEL", out var model) && model.Length is > 0 and < 512) return new("music/generate");
        return null;
    }

    internal static Dictionary<string, string> ReadTags(Stream input, bool includeMusic)
    {
        Span<byte> magic = stackalloc byte[12];
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var read = input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
        if (read < 4) return tags;
        input.Position = 0;
        if (magic[..3].SequenceEqual("ID3"u8)) ReadId3(input, tags, includeMusic);
        else if (magic[..4].SequenceEqual("fLaC"u8)) ReadFlac(input, tags, includeMusic);
        else if (magic[..4].SequenceEqual("OggS"u8)) ReadOgg(input, tags, includeMusic);
        else if (read >= 12 && magic[..4].SequenceEqual("RIFF"u8) && magic[8..].SequenceEqual("WAVE"u8)) ReadWave(input, tags, includeMusic);
        return tags;
    }

    private static int Synchsafe(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || value[..4].ToArray().Any(b => b > 127)) throw new InvalidDataException("Invalid ID3 size.");
        return (value[0] << 21) | (value[1] << 14) | (value[2] << 7) | value[3];
    }

    private static void ReadId3(Stream input, Dictionary<string, string> tags, bool includeMusic, long? containerEnd = null)
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
            var standard = includeMusic && type is "TIT2" or "TPE1" or "TALB" or "TCON" or "TBPM" or "TRCK" or "USLT" or "APIC";
            if (flags != 0 || type != "TXXX" && !standard || length > Limit || bytesRead + length > Limit)
            {
                input.Position += length;
                continue;
            }
            var body = new byte[length]; input.ReadExactly(body); bytesRead += length;
            if (type == "APIC")
            {
                ReadId3Picture(body, tags);
                continue;
            }
            if (standard && type != "USLT")
            {
                tags[type switch { "TIT2" => "TITLE", "TPE1" => "ARTIST", "TCON" => "GENRE", "TBPM" => "BPM",
                    "TRCK" => "TRACKNUMBER", _ => "ALBUM" }] =
                    Decode(body[0], body.AsSpan(1)).TrimEnd('\0');
                continue;
            }
            if (type == "USLT" && body.Length < 5) continue;
            var width = body[0] is 1 or 2 ? 2 : 1;
            var valueStart = type == "USLT" ? 4 : 1;
            var split = valueStart;
            while (split + width <= body.Length && (body[split] != 0 || width == 2 && body[split + 1] != 0)) split += width;
            if (split + width > body.Length) continue;
            if (type == "USLT")
            {
                tags.TryAdd("LYRICS", Decode(body[0], body.AsSpan(split + width)).TrimEnd('\0'));
                continue;
            }
            var name = Decode(body[0], body.AsSpan(valueStart, split - valueStart));
            if (name.StartsWith("LOPATA", StringComparison.OrdinalIgnoreCase) || includeMusic && IsSearchMetadata(name))
                tags[name] = Decode(body[0], body.AsSpan(split + width)).TrimEnd('\0');
        }
    }

    private static void ReadId3Picture(ReadOnlySpan<byte> body, Dictionary<string, string> tags)
    {
        if (body.Length < 8) return;
        var mimeEnd = body[1..].IndexOf((byte)0) + 1;
        if (mimeEnd < 2 || mimeEnd + 3 >= body.Length) return;
        var offset = mimeEnd + 2;
        var width = body[0] is 1 or 2 ? 2 : 1;
        while (offset + width <= body.Length && (body[offset] != 0 || width == 2 && body[offset + 1] != 0))
            offset += width;
        if (offset + width < body.Length) tags.TryAdd("COVER_BYTES", Convert.ToBase64String(body[(offset + width)..]));
    }

    private static string Decode(byte type, ReadOnlySpan<byte> value) => type switch
    {
        0 => Encoding.Latin1.GetString(value),
        1 => value.StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode.GetString(value[2..]) : Encoding.Unicode.GetString(value).TrimStart('\uFEFF'),
        2 => Encoding.BigEndianUnicode.GetString(value),
        3 => new UTF8Encoding(false, true).GetString(value),
        _ => throw new InvalidDataException("Invalid ID3 encoding.")
    };

    private static void ReadFlac(Stream input, Dictionary<string, string> tags, bool includeMusic)
    {
        input.Position = 4;
        Span<byte> header = stackalloc byte[4];
        for (var count = 0; count < 256 && input.Position + 4 <= input.Length; count++)
        {
            input.ReadExactly(header); var length = (header[1] << 16) | (header[2] << 8) | header[3];
            if (length > input.Length - input.Position) return;
            if ((header[0] & 0x7f) == 4 && length <= Limit)
            {
                var bytes = new byte[length]; input.ReadExactly(bytes); ReadComments(bytes, tags, includeMusic);
                if (!includeMusic) return;
            }
            else if (includeMusic && (header[0] & 0x7f) == 6 && length <= Limit && !tags.ContainsKey("METADATA_BLOCK_PICTURE"))
            {
                var bytes = new byte[length];
                input.ReadExactly(bytes);
                tags.TryAdd("METADATA_BLOCK_PICTURE", Convert.ToBase64String(bytes));
            }
            else input.Position += length;
            if ((header[0] & 0x80) != 0) return;
        }
    }

    private static void ReadComments(ReadOnlySpan<byte> bytes, Dictionary<string, string> tags, bool includeMusic)
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
            if (split <= 0) continue;
            var key = text[..split];
            if (key.StartsWith("LOPATA", StringComparison.OrdinalIgnoreCase) ||
                includeMusic && IsSearchMetadata(key))
                tags[key] = text[(split + 1)..];
        }
    }

    private static bool IsSearchMetadata(string name) => name.ToUpperInvariant() is
        "TITLE" or "ARTIST" or "ALBUM" or "ALBUMARTIST" or "VERSION" or "GENRE" or "BPM" or "TRACKNUMBER"
        or "MOOD" or "DESCRIPTION" or "COMMENT" or "VOCAL" or "LYRICS" or "UNSYNCEDLYRICS" or "SYNCEDLYRICS"
        or "METADATA_BLOCK_PICTURE";

    private static void ReadOgg(Stream input, Dictionary<string, string> tags, bool includeMusic)
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
                if (bytes.AsSpan().StartsWith("OpusTags"u8)) { ReadComments(bytes.AsSpan(8), tags, includeMusic); return; }
                if (bytes.Length >= 7 && bytes[0] == 3 && bytes.AsSpan(1, 6).SequenceEqual("vorbis"u8)) { ReadComments(bytes.AsSpan(7), tags, includeMusic); return; }
                packet.SetLength(0);
            }
        }
    }

    private static void ReadWave(Stream input, Dictionary<string, string> tags, bool includeMusic)
    {
        input.Position = 12; Span<byte> header = stackalloc byte[8];
        for (var count = 0; count < 4096 && input.Position + 8 <= input.Length; count++)
        {
            input.ReadExactly(header); var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (size > input.Length - input.Position) return;
            var end = input.Position + size + (size & 1);
            if (header[..4].SequenceEqual("id3 "u8) || header[..4].SequenceEqual("ID3 "u8))
                ReadId3(input, tags, includeMusic, input.Position + size);
            input.Position = end;
        }
    }
}
