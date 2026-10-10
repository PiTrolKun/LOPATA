using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

internal sealed record AudioCover(string Mime, byte Type, string Description, byte[] Data);
internal sealed record AudioConversionInfo(string Codec, int SampleRate, int Channels, int Bits, double Duration,
    Dictionary<string, string> Tags, IReadOnlyList<AudioMetadataBlock> Blocks, IReadOnlyList<AudioCover> Covers, JsonElement Probe);

internal static class AudioConversionMetadata
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = "TIT2", ["artist"] = "TPE1", ["album"] = "TALB", ["album_artist"] = "TPE2",
        ["composer"] = "TCOM", ["genre"] = "TCON", ["date"] = "TDRC", ["track"] = "TRCK",
        ["disc"] = "TPOS", ["copyright"] = "TCOP", ["publisher"] = "TPUB", ["lyrics"] = "USLT", ["comment"] = "COMM"
    };
    internal static async Task<AudioConversionInfo> ReadAsync(MusicAudioRuntime runtime, string path, CancellationToken token)
    {
        var json = await runtime.RunAsync(true, ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", path], token);
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var audio = streams.Where(s => Text(s, "codec_type") == "audio").ToArray();
        if (audio.Length != 1 || streams.Any(s => Text(s, "codec_type") != "audio"
            && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var p) && p.GetInt32() == 1)))
            throw new InvalidDataException("AudioShell.UnsupportedStreams");
        var a = audio[0]; var format = root.GetProperty("format");
        var tags = ProbeTags(root);
        var blocks = await Task.Run(() => AudioMetadataArchive.Read(path), token);
        var covers = new List<AudioCover>();
        foreach (var block in blocks)
        {
            if (block.Kind is "ID3v2" or "WAV:id3 " or "WAV:ID3 ") ReadId3(block.Bytes, tags, covers);
            if (block.Kind == "FLAC:4") ReadComments(block.Bytes, 0, tags, covers);
            if (block.Kind == "OpusTags") ReadComments(block.Bytes, 8, tags, covers);
            if (block.Kind == "FLAC:6") covers.Add(ReadPicture(block.Bytes));
        }
        var bits = Number(a, "bits_per_raw_sample"); if (bits == 0) bits = Number(a, "bits_per_sample");
        var duration = Text(format, "duration"); if (duration.Length == 0) duration = Text(a, "duration");
        return new(Text(a, "codec_name"), Number(a, "sample_rate"), Number(a, "channels"), bits,
            double.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0,
            tags, blocks, covers, root.Clone());
    }
    internal static Dictionary<string, string> ProbeTags(JsonElement root)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonElement value)
        {
            if (value.TryGetProperty("tags", out var t)) foreach (var pair in t.EnumerateObject()) tags[pair.Name] = pair.Value.GetString() ?? "";
        }
        if (root.TryGetProperty("format", out var format)) Add(format);
        if (root.TryGetProperty("streams", out var streams)) foreach (var stream in streams.EnumerateArray())
            if (Text(stream, "codec_type") == "audio") Add(stream);
        return tags;
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var v) ? v.ToString() : "";
    private static int Number(JsonElement value, string key) => int.TryParse(Text(value, key), out var n) ? n : 0;
    private static void ReadId3(byte[] bytes, Dictionary<string, string> tags, List<AudioCover> covers)
    {
        if (bytes.Length < 10 || !bytes.AsSpan().StartsWith("ID3"u8)) throw new InvalidDataException("AudioShell.InvalidContainer");
        var version = bytes[3];
        // Unsupported/encrypted/unsynchronised frames remain intact in the raw archive.
        if (version is not (3 or 4) || (bytes[5] & 0xC0) != 0) return;
        var end = Math.Min(bytes.Length, checked(10 + AudioMetadataArchive.Synchsafe(bytes.AsSpan(6, 4))));
        for (var pos = 10; pos + 10 <= end;)
        {
            if (bytes[pos] == 0) break;
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var length = version == 4 ? AudioMetadataArchive.Synchsafe(bytes.AsSpan(pos + 4, 4)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 4, 4)));
            if (length < 1 || length > end - pos - 10) throw new InvalidDataException("AudioShell.InvalidContainer");
            var flags = bytes[pos + 8] | bytes[pos + 9]; var body = bytes.AsSpan(pos + 10, length).ToArray(); pos += 10 + length;
            if (flags != 0) continue;
            if (id == "APIC")
            {
                var mimeEnd = Array.IndexOf(body, (byte)0, 1);
                if (mimeEnd < 0 || mimeEnd + 2 >= body.Length) throw new InvalidDataException("AudioShell.InvalidContainer");
                var mime = Encoding.Latin1.GetString(body, 1, mimeEnd - 1); var type = body[mimeEnd + 1];
                var offset = mimeEnd + 2; var description = Terminated(body, ref offset, body[0]);
                covers.Add(new(mime, type, description, body[offset..]));
            }
            else if (id[0] == 'T')
            {
                var offset = 1;
                if (id == "TXXX") { var key = Terminated(body, ref offset, body[0]); tags[key] = Decode(body[0], body.AsSpan(offset)).TrimEnd('\0'); }
                else
                {
                    var key = Ids.FirstOrDefault(p => p.Value == id).Key;
                    if (key is not null) tags[key] = Decode(body[0], body.AsSpan(1)).TrimEnd('\0');
                }
            }
            else if (id is "USLT" or "COMM" && body.Length >= 4)
            {
                var offset = 4; _ = Terminated(body, ref offset, body[0]);
                tags[id == "USLT" ? "lyrics" : "comment"] = Decode(body[0], body.AsSpan(offset)).TrimEnd('\0');
            }
        }
    }
    private static string Decode(byte encoding, ReadOnlySpan<byte> bytes) => encoding switch
    {
        0 => Encoding.Latin1.GetString(bytes), 1 => bytes.StartsWith(new byte[] { 0xFE, 0xFF })
            ? Encoding.BigEndianUnicode.GetString(bytes[2..]) : Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF'),
        2 => Encoding.BigEndianUnicode.GetString(bytes), 3 => Utf8.GetString(bytes), _ => throw new InvalidDataException("AudioShell.InvalidContainer")
    };
    private static string Terminated(byte[] bytes, ref int offset, byte encoding)
    {
        var start = offset; var width = encoding is 1 or 2 ? 2 : 1;
        while (offset + width <= bytes.Length)
        {
            if (bytes[offset] == 0 && (width == 1 || bytes[offset + 1] == 0))
            { var text = Decode(encoding, bytes.AsSpan(start, offset - start)); offset += width; return text; }
            offset += width;
        }
        throw new InvalidDataException("AudioShell.InvalidContainer");
    }
    private static void ReadComments(byte[] bytes, int offset, Dictionary<string, string> tags, List<AudioCover> covers)
    {
        using var memory = new MemoryStream(bytes); using var reader = new BinaryReader(memory); memory.Position = offset;
        byte[] Field()
        {
            var count = reader.ReadUInt32();
            if (count > memory.Length - memory.Position) throw new InvalidDataException("AudioShell.InvalidContainer");
            return reader.ReadBytes((int)count);
        }
        _ = Field(); var count = reader.ReadUInt32();
        if (count > (memory.Length - memory.Position) / 4) throw new InvalidDataException("AudioShell.InvalidContainer");
        for (var i = 0; i < count; i++)
        {
            var value = Utf8.GetString(Field()); var equals = value.IndexOf('=');
            if (equals < 1) continue;
            var key = value[..equals]; var text = value[(equals + 1)..];
            if (key.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase)) covers.Add(ReadPicture(Convert.FromBase64String(text)));
            else tags[key] = text;
        }
    }
    internal static AudioCover ReadPicture(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        uint UInt() { var data = reader.ReadBytes(4); if (data.Length != 4) throw new InvalidDataException("AudioShell.InvalidContainer"); return BinaryPrimitives.ReadUInt32BigEndian(data); }
        byte[] Field() { var size = UInt(); if (size > stream.Length - stream.Position) throw new InvalidDataException("AudioShell.InvalidContainer"); return reader.ReadBytes((int)size); }
        var type = UInt(); var mime = Utf8.GetString(Field()); var description = Utf8.GetString(Field());
        for (var i = 0; i < 4; i++) _ = UInt();
        return new(mime, checked((byte)type), description, Field());
    }
    internal static byte[] Picture(AudioCover cover)
    {
        using var output = new MemoryStream();
        void UInt(int n) { var data = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, checked((uint)n)); output.Write(data); }
        void Field(byte[] data) { UInt(data.Length); output.Write(data); }
        UInt(cover.Type); Field(Utf8.GetBytes(cover.Mime)); Field(Utf8.GetBytes(cover.Description));
        using var image = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(cover.Data));
        UInt(image?.Info.Width ?? 0); UInt(image?.Info.Height ?? 0); UInt(24); UInt(0); Field(cover.Data);
        return output.ToArray();
    }
    internal static string Ffmetadata(IReadOnlyDictionary<string, string> tags, IEnumerable<AudioCover> covers)
    {
        var text = new StringBuilder(MusicSongMetadata.FfMetadata(tags));
        foreach (var cover in covers) text.Append("METADATA_BLOCK_PICTURE=").Append(Convert.ToBase64String(Picture(cover))).Append('\n');
        return text.ToString();
    }
    internal static byte[] Id3(IReadOnlyDictionary<string, string> tags, IEnumerable<AudioCover> covers)
    {
        using var frames = new MemoryStream();
        void Frame(string id, byte[] body) { frames.Write(Encoding.ASCII.GetBytes(id)); frames.Write(Size(body.Length)); frames.Write([0, 0]); frames.Write(body); }
        foreach (var (key, value) in tags)
        {
            var id = Ids.GetValueOrDefault(key, "TXXX");
            Frame(id, Utf8.GetBytes(id is "USLT" or "COMM" ? "\u0003xxx\0" + value : id == "TXXX" ? "\u0003" + key + "\0" + value : "\u0003" + value));
        }
        foreach (var cover in covers)
        {
            using var body = new MemoryStream(); body.WriteByte(3); body.Write(Encoding.Latin1.GetBytes(cover.Mime)); body.WriteByte(0);
            body.WriteByte(cover.Type); body.Write(Utf8.GetBytes(cover.Description)); body.WriteByte(0); body.Write(cover.Data); Frame("APIC", body.ToArray());
        }
        using var result = new MemoryStream(); result.Write("ID3"u8); result.Write([4, 0, 0]); result.Write(Size((int)frames.Length)); frames.Position = 0; frames.CopyTo(result); return result.ToArray();
    }
    private static byte[] Size(int n) => [(byte)((n >> 21) & 127), (byte)((n >> 14) & 127), (byte)((n >> 7) & 127), (byte)(n & 127)];
}
