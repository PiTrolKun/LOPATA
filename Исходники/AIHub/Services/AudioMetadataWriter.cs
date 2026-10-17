using System.Buffers.Binary;
using System.IO;

namespace AIHub.Services;

/// <summary>Container-only tags/artwork writer shared by music output and audio conversion.</summary>
internal static class AudioMetadataWriter
{
    // Opus picture comments must be supplied with ffmetadata at muxing time.
    internal static async Task AttachAsync(string path, MusicAudioFormat format,
        IReadOnlyDictionary<string, string> tags, IReadOnlyList<AudioCover> covers, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (format == MusicAudioFormat.Opus) return;
        if (format == MusicAudioFormat.Wav)
        {
            var tag = AudioConversionMetadata.Id3(tags, covers);
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[12]; file.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new InvalidDataException("Invalid PCM WAV.");
            file.Position = file.Length; file.Write("id3 "u8);
            var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)tag.Length)); file.Write(size);
            await file.WriteAsync(tag, token); if ((tag.Length & 1) != 0) file.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(file.Length - 8)));
            file.Position = 4; file.Write(size); file.Flush(true);
            return;
        }
        if (format == MusicAudioFormat.Flac && covers.Count == 0) return;
        if (format is not (MusicAudioFormat.Mp3 or MusicAudioFormat.Flac))
            throw new InvalidDataException("Unknown audio format.");
        var next = path + ".tags";
        var ownsNext = false;
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsNext = true;
                if (format == MusicAudioFormat.Mp3)
                    await output.WriteAsync(AudioConversionMetadata.Id3(tags, covers), token);
                else
                {
                    using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
                    if (!reader.ReadBytes(4).AsSpan().SequenceEqual("fLaC"u8))
                        throw new InvalidDataException("AudioShell.InvalidContainer");
                    output.Write("fLaC"u8); bool last;
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        var header = reader.ReadBytes(4);
                        if (header.Length != 4) throw new InvalidDataException("AudioShell.InvalidContainer");
                        last = (header[0] & 128) != 0; header[0] &= 127; output.Write(header);
                        var size = (header[1] << 16) | (header[2] << 8) | header[3];
                        var data = reader.ReadBytes(size);
                        if (data.Length != size) throw new InvalidDataException("AudioShell.InvalidContainer");
                        output.Write(data);
                    } while (!last);
                    for (var i = 0; i < covers.Count; i++)
                    {
                        var picture = AudioConversionMetadata.Picture(covers[i]);
                        if (picture.Length >= 1 << 24) throw new InvalidDataException("AudioShell.MetadataTooLarge");
                        output.Write([(byte)(6 | (i == covers.Count - 1 ? 128 : 0)),
                            (byte)(picture.Length >> 16), (byte)(picture.Length >> 8), (byte)picture.Length]);
                        await output.WriteAsync(picture, token);
                    }
                }
                await input.CopyToAsync(output, token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(next, path, true);
        }
        finally { if (ownsNext && File.Exists(next)) File.Delete(next); }
    }
}
