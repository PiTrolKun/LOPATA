using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>ID3v2.4 UTF-8 frames, shared by MP3 and the WAV id3 chunk.</summary>
public static class MusicId3
{
    public static byte[] Tag(IReadOnlyDictionary<string, string> values)
    {
        using var frames = new MemoryStream();
        foreach (var (key, value) in values) {
            var id = key.ToLowerInvariant() switch {
                "title" => "TIT2", "artist" => "TPE1", "genre" => "TCON", "date" => "TDRC", "track" => "TRCK",
                "lyrics" => "USLT", "comment" => "COMM", _ => "TXXX" };
            var body = Encoding.UTF8.GetBytes(id is "USLT" or "COMM" ? "\u0003xxx\0" + value :
                id == "TXXX" ? "\u0003" + key + "\0" + value : "\u0003" + value);
            frames.Write(Encoding.ASCII.GetBytes(id)); frames.Write(Size(body.Length)); frames.Write([0, 0]); frames.Write(body);
        }
        using var tag = new MemoryStream(); tag.Write("ID3"u8); tag.Write([4, 0, 0]); tag.Write(Size(checked((int)frames.Length)));
        frames.Position = 0; frames.CopyTo(tag); return tag.ToArray();
    }
    private static byte[] Size(int count) => [(byte)((count >> 21) & 127), (byte)((count >> 14) & 127), (byte)((count >> 7) & 127), (byte)(count & 127)];
    public static async Task AttachAsync(string path, MusicAudioFormat format, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        var tag = Tag(values);
        if (format == MusicAudioFormat.Wav) {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[12]; file.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("Invalid PCM WAV.");
            file.Position = file.Length; file.Write("id3 "u8);
            var length = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)tag.Length); file.Write(length);
            await file.WriteAsync(tag, token); if ((tag.Length & 1) != 0) file.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)(file.Length - 8))); file.Position = 4; file.Write(length); file.Flush(true);
        }
        else if (format == MusicAudioFormat.Mp3) {
            var temporary = path + ".id3";
            try {
                using (var source = File.OpenRead(path))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await output.WriteAsync(tag, token); await source.CopyToAsync(output, token); output.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
