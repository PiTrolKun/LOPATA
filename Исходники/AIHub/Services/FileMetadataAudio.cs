using System.Buffers.Binary;
using System.IO;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

internal static class FileMetadataAudio
{
    internal static readonly Dictionary<string, string> Frames = new(StringComparer.OrdinalIgnoreCase)
    { ["title"]="TIT2", ["artist"]="TPE1", ["album"]="TALB", ["genre"]="TCON", ["date"]="TDRC",
      ["track"]="TRCK", ["comment"]="COMM", ["copyright"]="TCOP", ["composer"]="TCOM" };

    internal static async Task WriteAsync(string source, string destination, FileMetadataPatch patch,
        Dictionary<string, string> provenance, CancellationToken token)
    {
        var bytes = await MusicLyricsTimingMetadata.ReadFile(source, token);
        var before = MusicLyricsTimingMetadata.PayloadHash(bytes);
        var values = patch.Values.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        byte[] result;
        if (bytes.AsSpan().StartsWith("fLaC"u8) || bytes.AsSpan().StartsWith("OggS"u8))
            result = MusicLyricsTimingMetadata.Rewrite(bytes, values);
        else if (bytes.AsSpan().StartsWith("RIFF"u8))
        {
            var chunks = MusicLyricsTimingMetadata.Wave(bytes);
            if (chunks.Count(c => c.Kind is "id3 " or "ID3 ") > 1) throw new InvalidDataException("Metadata.Ambiguous");
            var index = chunks.FindIndex(c => c.Kind is "id3 " or "ID3 ");
            var tag = EditId3(index < 0 ? [] : chunks[index].Body, values);
            if (index < 0) chunks.Add(("id3 ", tag, 0)); else chunks[index] = (chunks[index].Kind, tag, 0);
            result = Wave(bytes, chunks);
        }
        else
        {
            var offset = MusicLyricsTimingMetadata.Id3End(bytes);
            using var output = new MemoryStream(); output.Write(EditId3(bytes[..offset], values)); output.Write(bytes.AsSpan(offset)); result = output.ToArray();
        }
        if (provenance.Count > 0) result = MusicLyricsTimingMetadata.Rewrite(result, provenance);
        if (before != MusicLyricsTimingMetadata.PayloadHash(result)) throw new InvalidDataException("Metadata.ContentChanged");
        await File.WriteAllBytesAsync(destination, result, token);
        if (patch.Cover is { } cover)
        {
            // The shared publisher verifies tags and audio. Multiple pictures require a targeted writer,
            // so the caller rejects that case rather than dropping secondary artwork.
            var next = destination + ".cover";
            try
            {
                await MusicCoverMetadata.WriteCopyAsync(destination, next, new(cover.Mime, 3, "", cover.Bytes), token);
                File.Move(next, destination, true);
            }
            finally { if (File.Exists(next)) File.Delete(next); }
        }
    }
    private static byte[] Wave(byte[] original, List<(string Kind, byte[] Body, byte Pad)> chunks)
    {
        using var output = new MemoryStream(); output.Write(original.AsSpan(0, 12));
        foreach (var chunk in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(chunk.Kind)); var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)chunk.Body.Length)); output.Write(size); output.Write(chunk.Body);
            if ((chunk.Body.Length & 1) != 0) output.WriteByte(chunk.Pad);
        }
        var bytes = output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)(bytes.Length - 8))); return bytes;
    }
    private static byte[] EditId3(byte[] existing, Dictionary<string, string> changes)
    {
        if (changes.Keys.Any(k => !Frames.ContainsKey(k))) throw new InvalidDataException("Metadata.Protected");
        var end = existing.Length == 0 ? 0 : MusicLyricsTimingMetadata.Id3End(existing);
        var version = end == 0 ? (byte)4 : existing[3];
        using var frames = new MemoryStream(); var seen = new HashSet<string>();
        for (var pos = 10; pos + 10 <= end && existing[pos] != 0;)
        {
            var id = Encoding.ASCII.GetString(existing, pos, 4);
            var length = version == 4 ? AudioMetadataArchive.Synchsafe(existing.AsSpan(pos + 4, 4)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(existing.AsSpan(pos + 4, 4)));
            if (length < 1 || length > end - pos - 10) throw new InvalidDataException("Metadata.ReadFailed");
            var key = Frames.FirstOrDefault(p => p.Value == id).Key;
            if (key is not null && changes.ContainsKey(key))
            {
                if (!seen.Add(key) || existing[pos + 8] != 0 || existing[pos + 9] != 0) throw new InvalidDataException("Metadata.Ambiguous");
            }
            else frames.Write(existing.AsSpan(pos, length + 10));
            pos += length + 10;
        }
        foreach (var (key, text) in changes)
        {
            var id = Frames[key]; byte[] body;
            if (version == 4) body = Encoding.UTF8.GetBytes(id == "COMM" ? "\u0003xxx\0" + text : "\u0003" + text);
            else
            {
                using var value = new MemoryStream(); value.WriteByte(1);
                if (id == "COMM") { value.Write("xxx"u8); value.Write([255,254,0,0]); }
                value.Write([255,254]); value.Write(Encoding.Unicode.GetBytes(text)); body = value.ToArray();
            }
            frames.Write(Encoding.ASCII.GetBytes(id)); var size = new byte[4];
            if (version == 4) Sync(size, body.Length); else BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
            frames.Write(size); frames.Write([0,0]); frames.Write(body);
        }
        using var tag = new MemoryStream(); tag.Write("ID3"u8); tag.Write([version,0,0]); var total = new byte[4];
        Sync(total, checked((int)frames.Length)); tag.Write(total); frames.Position = 0; frames.CopyTo(tag); return tag.ToArray();
    }
    private static void Sync(Span<byte> bytes, int value)
    { bytes[0]=(byte)((value>>21)&127); bytes[1]=(byte)((value>>14)&127); bytes[2]=(byte)((value>>7)&127); bytes[3]=(byte)(value&127); }
}
