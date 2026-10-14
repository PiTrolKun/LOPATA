using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Edits container tags only. Compressed audio packets/PCM and existing metadata stay byte-identical.</summary>
public static class MusicLyricsTimingMetadata
{
    public const string JsonTag = "LOPATA_TIMED_LYRICS";
    public const string LrcTag = "LOPATA_LRC";
    private const int MaximumFileBytes = 512 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static async Task<string> AudioPayloadHashAsync(string path, CancellationToken token) =>
        PayloadHash(await ReadFile(path, token));
    public static async Task<MusicTimedLyrics?> ReadAsync(string path, CancellationToken token)
    {
        var tags = Tags(await ReadFile(path, token));
        if (!tags.TryGetValue(JsonTag, out var json)) return null;
        var value = JsonSerializer.Deserialize<MusicTimedLyrics>(json) ?? throw new InvalidDataException("Empty timed lyrics metadata.");
        value.Validate(); return value;
    }
    public static async Task WriteCopyAsync(string source, string destination, MusicTimedLyrics value, CancellationToken token)
    {
        value.Validate(); var bytes = await ReadFile(source, token);
        if (!PayloadHash(bytes).Equals(value.AudioPayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Timed lyrics belong to different audio.");
        var values = new Dictionary<string, string> { [JsonTag] = JsonSerializer.Serialize(value), [LrcTag] = Lrc(value) };
        var result = Rewrite(bytes, values);
        if (PayloadHash(result) != PayloadHash(bytes)) throw new InvalidDataException("Timed metadata changed audio payload.");
        var read = Tags(result);
        foreach (var pair in values) if (!read.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
            throw new InvalidDataException("Timed metadata round-trip failed.");
        foreach (var pair in Tags(bytes)) if (!values.ContainsKey(pair.Key) && (!read.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            throw new InvalidDataException("Timed metadata lost an existing tag.");
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(result, token); file.Flush(true);
    }
    public static string Lrc(MusicTimedLyrics value)
    {
        var text = new StringBuilder("[re:LOPATA]\n");
        foreach (var line in value.Lines) {
            var hundredths = (long)Math.Round(line.StartSeconds * 100, MidpointRounding.AwayFromZero);
            text.Append('[').Append((hundredths / 6000).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append((hundredths / 100 % 60).ToString("00", CultureInfo.InvariantCulture)).Append('.')
                .Append((hundredths % 100).ToString("00", CultureInfo.InvariantCulture)).Append(']')
                .Append(value.SourceLyrics.AsSpan(line.SourceStart, line.SourceLength)).Append('\n');
        }
        return text.ToString();
    }
    private static async Task<byte[]> ReadFile(string path, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        if (file.Length is < 12 or > MaximumFileBytes) throw new InvalidDataException("Unsupported timed metadata container size.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, token); return bytes;
    }
    private static string PayloadHash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (bytes.AsSpan().StartsWith("fLaC"u8)) {
            var blocks = Flac(bytes, out var end);
            hash.AppendData("FLAC"u8); hash.AppendData(blocks.Single(b => b.Kind == 0).Body); hash.AppendData(bytes.AsSpan(end));
        }
        else if (bytes.AsSpan().StartsWith("RIFF"u8)) {
            hash.AppendData("WAV"u8);
            foreach (var chunk in Wave(bytes)) if (chunk.Kind is "fmt " or "data") hash.AppendData(chunk.Body);
        }
        else if (bytes.AsSpan().StartsWith("OggS"u8)) {
            hash.AppendData("OPUS"u8); var packets = OggPackets(Ogg(bytes));
            if (packets.Count < 3 || !packets[0].AsSpan().StartsWith("OpusHead"u8) || !packets[1].AsSpan().StartsWith("OpusTags"u8))
                throw new InvalidDataException("Only single-stream Opus supports timed metadata.");
            hash.AppendData(packets[0]); foreach (var packet in packets.Skip(2)) hash.AppendData(packet);
        }
        else {
            hash.AppendData("MP3"u8); var start = Id3End(bytes); var end = bytes.Length;
            if (end >= 128 && bytes.AsSpan(end - 128, 3).SequenceEqual("TAG"u8)) end -= 128;
            if (end >= 32 && bytes.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8)) {
                var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end - 20, 4)));
                if (size < 32 || size > end - start) throw new InvalidDataException("Invalid APE metadata.");
                end -= size;
                if (end - start >= 32 && bytes.AsSpan(end - 32, 8).SequenceEqual("APETAGEX"u8)) end -= 32;
            }
            if (end <= start || bytes[start] != 255 || (bytes[start + 1] & 224) != 224) throw new InvalidDataException("Unsupported audio container.");
            hash.AppendData(bytes.AsSpan(start, end - start));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static byte[] Rewrite(byte[] bytes, Dictionary<string, string> values)
    {
        if (bytes.AsSpan().StartsWith("fLaC"u8)) {
            var blocks = Flac(bytes, out var end); var index = blocks.FindIndex(b => b.Kind == 4);
            var body = Comments(index >= 0 ? blocks[index].Body : EmptyComments(), 0, values);
            if (body.Length >= 1 << 24) throw new InvalidDataException("FLAC timing metadata exceeds block limit.");
            if (index >= 0) blocks[index] = (4, body); else blocks.Add((4, body));
            using var output = new MemoryStream(); output.Write("fLaC"u8);
            for (var i = 0; i < blocks.Count; i++) {
                var block = blocks[i]; output.Write([(byte)(block.Kind | (i == blocks.Count - 1 ? 128 : 0)),
                    (byte)(block.Body.Length >> 16), (byte)(block.Body.Length >> 8), (byte)block.Body.Length]); output.Write(block.Body);
            }
            output.Write(bytes.AsSpan(end)); return output.ToArray();
        }
        if (bytes.AsSpan().StartsWith("RIFF"u8)) {
            var chunks = Wave(bytes); var index = chunks.FindIndex(c => c.Kind is "id3 " or "ID3 ");
            var tag = AppendId3(index >= 0 ? chunks[index].Body : [], values);
            if (index >= 0) chunks[index] = (chunks[index].Kind, tag, 0); else chunks.Add(("id3 ", tag, 0));
            using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, 12));
            foreach (var chunk in chunks) { output.Write(Encoding.ASCII.GetBytes(chunk.Kind)); WriteUInt(output, (uint)chunk.Body.Length);
                output.Write(chunk.Body); if ((chunk.Body.Length & 1) != 0) output.WriteByte(chunk.Pad); }
            var result = output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(result.Length - 8)); return result;
        }
        if (bytes.AsSpan().StartsWith("OggS"u8)) return RewriteOgg(bytes, values);
        var offset = Id3End(bytes); var id3 = AppendId3(bytes[..offset], values);
        using var mp3 = new MemoryStream(); mp3.Write(id3); mp3.Write(bytes.AsSpan(offset)); return mp3.ToArray();
    }
    private static int Id3End(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith("ID3"u8)) return 0;
        if (bytes[3] is not (3 or 4) || bytes[5] != 0) throw new InvalidDataException("Unsupported ID3 flags/version; original audio kept intact.");
        var end = checked(10 + AudioMetadataArchive.Synchsafe(bytes.AsSpan(6, 4)));
        if (end > bytes.Length) throw new InvalidDataException("Truncated ID3 metadata."); return end;
    }
    private static byte[] AppendId3(byte[] existing, Dictionary<string, string> values)
    {
        var oldEnd = existing.Length == 0 ? 0 : Id3End(existing); var version = oldEnd == 0 ? 4 : existing[3];
        using var frames = new MemoryStream();
        for (var pos = 10; pos + 10 <= oldEnd && existing[pos] != 0;) {
            var size = version == 4 ? AudioMetadataArchive.Synchsafe(existing.AsSpan(pos + 4, 4)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(existing.AsSpan(pos + 4, 4)));
            if (size < 1 || size > oldEnd - pos - 10) throw new InvalidDataException("Invalid ID3 frame.");
            var body = existing.AsSpan(pos + 10, size); var key = Id3TextKey(existing.AsSpan(pos, 4), body);
            if (key is null || !values.ContainsKey(key)) frames.Write(existing.AsSpan(pos, size + 10)); pos += size + 10;
        }
        foreach (var pair in values) {
            var body = version == 4 ? Utf8.GetBytes("\u0003" + pair.Key + "\0" + pair.Value)
                : Encoding.Unicode.GetBytes(pair.Key + "\0" + pair.Value);
            if (version == 3) body = new byte[] { 1, 255, 254 }.Concat(body).ToArray();
            frames.Write("TXXX"u8); var size = new byte[4];
            if (version == 4) SyncSize(size, body.Length); else BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
            frames.Write(size); frames.Write([0, 0]); frames.Write(body);
        }
        using var tag = new MemoryStream(); tag.Write("ID3"u8); tag.Write([(byte)version, 0, 0]); var length = new byte[4]; SyncSize(length, (int)frames.Length);
        tag.Write(length); frames.Position = 0; frames.CopyTo(tag); return tag.ToArray();
    }
    private static string? Id3TextKey(ReadOnlySpan<byte> id, ReadOnlySpan<byte> body)
    {
        if (!id.SequenceEqual("TXXX"u8) || body.Length < 2) return null;
        if (body[0] == 3 || body[0] == 0) { var end = body[1..].IndexOf((byte)0); return end < 0 ? null : (body[0] == 3 ? Utf8 : Encoding.Latin1).GetString(body.Slice(1, end)); }
        if (body[0] != 1 || body.Length < 3 || body[1] != 255 || body[2] != 254) return null;
        for (var end = 3; end + 1 < body.Length; end += 2) if (body[end] == 0 && body[end + 1] == 0) return Encoding.Unicode.GetString(body.Slice(3, end - 3));
        return null;
    }
    private static Dictionary<string, string> Tags(byte[] bytes)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bytes.AsSpan().StartsWith("fLaC"u8)) { foreach (var block in Flac(bytes, out _).Where(b => b.Kind == 4)) ReadComments(block.Body, 0, tags); }
        else if (bytes.AsSpan().StartsWith("OggS"u8)) ReadComments(OggPackets(Ogg(bytes))[1], 8, tags);
        else if (bytes.AsSpan().StartsWith("RIFF"u8)) { foreach (var chunk in Wave(bytes).Where(c => c.Kind is "id3 " or "ID3 ")) ReadId3(chunk.Body, tags); }
        else if (Id3End(bytes) is var end && end > 0) ReadId3(bytes[..end], tags);
        return tags;
    }
    private static void ReadId3(byte[] bytes, Dictionary<string, string> tags)
    {
        var end = Id3End(bytes);
        for (var pos = 10; pos + 10 <= end && bytes[pos] != 0;) {
            var length = bytes[3] == 4 ? AudioMetadataArchive.Synchsafe(bytes.AsSpan(pos + 4, 4)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 4, 4)));
            if (length < 1 || length > end - pos - 10) throw new InvalidDataException("Invalid ID3 frame.");
            var key = Id3TextKey(bytes.AsSpan(pos, 4), bytes.AsSpan(pos + 10, length));
            if (key is not null) {
                var body = bytes.AsSpan(pos + 10, length);
                var text = body[0] switch { 3 => Utf8.GetString(body[1..]), 0 => Encoding.Latin1.GetString(body[1..]), 1 => Encoding.Unicode.GetString(body[3..]), _ => "" };
                var separator = text.IndexOf('\0'); if (separator >= 0) tags[key] = text[(separator + 1)..];
            }
            pos += length + 10;
        }
    }
    private static List<(int Kind, byte[] Body)> Flac(byte[] bytes, out int end)
    {
        var result = new List<(int, byte[])>(); var pos = 4;
        while (true) { if (pos + 4 > bytes.Length) throw new InvalidDataException("Truncated FLAC block.");
            var head = bytes[pos]; var size = (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3]; pos += 4;
            if (size > bytes.Length - pos) throw new InvalidDataException("Truncated FLAC block.");
            result.Add((head & 127, bytes.AsSpan(pos, size).ToArray())); pos += size; if ((head & 128) != 0) break; }
        end = pos; return result;
    }
    private static List<(string Kind, byte[] Body, byte Pad)> Wave(byte[] bytes)
    {
        if (!bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8)
            throw new InvalidDataException("Unsupported WAV container.");
        var result = new List<(string, byte[], byte)>();
        for (var pos = 12; pos < bytes.Length;) { if (pos + 8 > bytes.Length) throw new InvalidDataException("Truncated WAV chunk.");
            var kind = Encoding.ASCII.GetString(bytes, pos, 4); var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4))); pos += 8;
            if (size > bytes.Length - pos || (size & 1) != 0 && pos + size == bytes.Length) throw new InvalidDataException("Truncated WAV chunk.");
            result.Add((kind, bytes.AsSpan(pos, size).ToArray(), (size & 1) != 0 ? bytes[pos + size] : (byte)0)); pos += size + (size & 1); }
        return result;
    }
    private static byte[] EmptyComments()
    { using var output = new MemoryStream(); var vendor = Utf8.GetBytes("LOPATA"); WriteUInt(output, (uint)vendor.Length); output.Write(vendor); WriteUInt(output, 0); return output.ToArray(); }
    private static (byte[] Vendor, List<byte[]> Entries, byte[] Tail) ParseComments(byte[] bytes, int offset)
    {
        using var stream = new MemoryStream(bytes); stream.Position = offset; using var reader = new BinaryReader(stream);
        byte[] Field() { var size = reader.ReadUInt32(); if (size > stream.Length - stream.Position) throw new InvalidDataException("Invalid comment field."); return reader.ReadBytes((int)size); }
        var vendor = Field(); var count = reader.ReadUInt32(); if (count > (stream.Length - stream.Position) / 4) throw new InvalidDataException("Invalid comment count.");
        var entries = new List<byte[]>(); for (var i = 0; i < count; i++) entries.Add(Field()); return (vendor, entries, reader.ReadBytes((int)(stream.Length - stream.Position)));
    }
    private static void ReadComments(byte[] bytes, int offset, Dictionary<string, string> tags)
    { foreach (var entry in ParseComments(bytes, offset).Entries) { var text = Utf8.GetString(entry); var split = text.IndexOf('='); if (split > 0) tags[text[..split]] = text[(split + 1)..]; } }
    private static byte[] Comments(byte[] bytes, int offset, Dictionary<string, string> values)
    {
        var (vendor, entries, tail) = ParseComments(bytes, offset);
        entries.RemoveAll(entry => { var text = Utf8.GetString(entry); var equals = text.IndexOf('='); return equals > 0 && values.ContainsKey(text[..equals]); });
        entries.AddRange(values.Select(pair => Utf8.GetBytes(pair.Key + "=" + pair.Value)));
        using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, offset)); WriteUInt(output, (uint)vendor.Length); output.Write(vendor); WriteUInt(output, (uint)entries.Count);
        foreach (var entry in entries) { WriteUInt(output, (uint)entry.Length); output.Write(entry); } output.Write(tail); return output.ToArray();
    }
    private static void WriteUInt(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); stream.Write(bytes); }
    private static void SyncSize(Span<byte> bytes, int count) { bytes[0] = (byte)((count >> 21) & 127); bytes[1] = (byte)((count >> 14) & 127); bytes[2] = (byte)((count >> 7) & 127); bytes[3] = (byte)(count & 127); }
    private static List<byte[]> Ogg(byte[] bytes)
    {
        var pages = new List<byte[]>(); uint? serial = null; uint? sequence = null;
        for (var pos = 0; pos < bytes.Length;) {
            if (pos + 27 > bytes.Length || !bytes.AsSpan(pos, 4).SequenceEqual("OggS"u8) || bytes[pos + 4] != 0) throw new InvalidDataException("Invalid Ogg page.");
            var count = bytes[pos + 26]; if (pos + 27 + count > bytes.Length) throw new InvalidDataException("Truncated Ogg page.");
            var size = 27 + count + bytes.AsSpan(pos + 27, count).ToArray().Sum(b => b);
            if (size > bytes.Length - pos) throw new InvalidDataException("Truncated Ogg payload.");
            var page = bytes.AsSpan(pos, size).ToArray(); var id = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14)); var seq = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(18));
            if (serial is { } previous && id != previous || sequence is { } last && seq != last + 1) throw new InvalidDataException("Unsupported chained/multiplexed Ogg.");
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22)); page.AsSpan(22, 4).Clear();
            if (crc != Crc(page)) throw new InvalidDataException("Ogg checksum mismatch."); BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
            serial = id; sequence = seq; pages.Add(page); pos += size;
        }
        return pages;
    }
    private static List<byte[]> OggPackets(List<byte[]> pages)
    {
        var packets = new List<byte[]>(); using var packet = new MemoryStream();
        foreach (var page in pages) { var pos = 27 + page[26]; foreach (var length in page.AsSpan(27, page[26]).ToArray()) {
                packet.Write(page.AsSpan(pos, length)); pos += length;
                if (length != 255) { packets.Add(packet.ToArray()); packet.SetLength(0); }
            } }
        if (packet.Length != 0) throw new InvalidDataException("Incomplete Opus packet."); return packets;
    }
    private static byte[] RewriteOgg(byte[] bytes, Dictionary<string, string> values)
    {
        var pages = Ogg(bytes); var packets = OggPackets(pages);
        if (packets.Count < 3 || !packets[0].AsSpan().StartsWith("OpusHead"u8) || !packets[1].AsSpan().StartsWith("OpusTags"u8)) throw new InvalidDataException("Unsupported Ogg codec.");
        // Keep all audio pages including granule positions. Our encoder isolates the two header packets.
        var completions = 0; var lastTagPage = -1;
        for (var i = 0; i < pages.Count && lastTagPage < 0; i++) {
            var laces = pages[i].AsSpan(27, pages[i][26]);
            for (var j = 0; j < laces.Length; j++) if (laces[j] < 255 && ++completions == 2) {
                if (j != laces.Length - 1) throw new InvalidDataException("Opus comment and audio share a page; original kept intact."); lastTagPage = i; break;
            }
        }
        if (lastTagPage < 1 || pages[0][26] != packets[0].Length / 255 + 1) throw new InvalidDataException("Unsupported Opus header layout.");
        var comment = Comments(packets[1], 8, values); var segments = new List<byte>();
        for (var remaining = comment.Length; remaining >= 255; remaining -= 255) segments.Add(255); segments.Add((byte)(comment.Length % 255));
        using var output = new MemoryStream(); output.Write(pages[0]); var sequence = BinaryPrimitives.ReadUInt32LittleEndian(pages[0].AsSpan(18)) + 1;
        var offset = 0;
        for (var first = 0; first < segments.Count; first += 255) {
            var lengths = segments.Skip(first).Take(255).ToArray(); var size = lengths.Sum(b => b); var page = new byte[27 + lengths.Length + size];
            "OggS"u8.CopyTo(page); page[5] = first == 0 ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), first + lengths.Length < segments.Count ? -1 : 0);
            pages[0].AsSpan(14, 4).CopyTo(page.AsSpan(14)); BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence++);
            page[26] = (byte)lengths.Length; lengths.CopyTo(page, 27); comment.AsSpan(offset, size).CopyTo(page.AsSpan(27 + lengths.Length)); offset += size;
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page)); output.Write(page);
        }
        foreach (var page in pages.Skip(lastTagPage + 1)) {
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence++); page.AsSpan(22, 4).Clear(); BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page)); output.Write(page);
        }
        return output.ToArray();
    }
    private static uint Crc(byte[] bytes)
    {
        uint crc = 0;
        foreach (var b in bytes) { crc ^= (uint)b << 24; for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1; }
        return crc;
    }
}
