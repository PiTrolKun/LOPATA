using System.Buffers.Binary;
using System.IO;

namespace AIHub.Services;

public sealed record AudioMetadataBlock(string Kind, byte[] Bytes);

/// <summary>Lossless backup of container metadata, independent of FFmpeg's tag vocabulary.</summary>
internal static class AudioMetadataArchive
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal static IReadOnlyList<AudioMetadataBlock> Read(string path)
    {
        using var input = File.OpenRead(path); using var reader = new BinaryReader(input);
        var blocks = new List<AudioMetadataBlock>(); var total = 0;
        void Add(string kind, int count)
        {
            if (count < 0 || (long)count > input.Length - input.Position || count > MaximumBytes - total)
                throw new InvalidDataException("AudioShell.MetadataTooLarge");
            var bytes = reader.ReadBytes(count); total += count; blocks.Add(new(kind, bytes));
        }
        var head = reader.ReadBytes(12); input.Position = 0;
        if (head.AsSpan().StartsWith("RIFF"u8) && head.AsSpan(8).StartsWith("WAVE"u8))
        {
            input.Position = 12;
            while (input.Position < input.Length)
            {
                if (input.Length - input.Position < 8) throw new InvalidDataException("AudioShell.InvalidContainer");
                var header = reader.ReadBytes(8); var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (size > input.Length - input.Position) throw new InvalidDataException("AudioShell.InvalidContainer");
                var kind = System.Text.Encoding.ASCII.GetString(header, 0, 4);
                if (kind == "data") input.Position += size;
                else Add("WAV:" + kind, checked((int)size));
                if ((size & 1) != 0) input.Position++;
            }
        }
        else if (head.AsSpan().StartsWith("fLaC"u8))
        {
            input.Position = 4; bool last;
            do
            {
                var header = reader.ReadBytes(4);
                if (header.Length != 4) throw new InvalidDataException("AudioShell.InvalidContainer");
                last = (header[0] & 128) != 0;
                Add("FLAC:" + (header[0] & 127), (header[1] << 16) | (header[2] << 8) | header[3]);
            } while (!last);
        }
        else if (head.AsSpan().StartsWith("OggS"u8))
        {
            // Opus metadata lives in the first two complete packets; never copy audio packets.
            using var packet = new MemoryStream(); var packets = 0; uint? serial = null;
            while (packets < 2)
            {
                var page = reader.ReadBytes(27);
                if (page.Length != 27 || !page.AsSpan().StartsWith("OggS"u8) || page[4] != 0)
                    throw new InvalidDataException("AudioShell.InvalidContainer");
                var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14));
                serial ??= pageSerial;
                if (serial != pageSerial) throw new InvalidDataException("AudioShell.UnsupportedStreams");
                var lengths = reader.ReadBytes(page[26]);
                if (lengths.Length != page[26]) throw new InvalidDataException("AudioShell.InvalidContainer");
                foreach (var length in lengths)
                {
                    if (packet.Length + length > MaximumBytes - total) throw new InvalidDataException("AudioShell.MetadataTooLarge");
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length) throw new InvalidDataException("AudioShell.InvalidContainer");
                    packet.Write(bytes);
                    if (length == 255) continue;
                    var payload = packet.ToArray();
                    if (!payload.AsSpan().StartsWith(packets == 0 ? "OpusHead"u8 : "OpusTags"u8))
                        throw new InvalidDataException("AudioShell.UnsupportedFile");
                    blocks.Add(new(packets == 0 ? "OpusHead" : "OpusTags", payload)); total += payload.Length;
                    packet.SetLength(0); packets++;
                    if (packets == 2) break;
                }
            }
        }
        else if (head.AsSpan().StartsWith("ID3"u8) || Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (head.AsSpan().StartsWith("ID3"u8))
            {
                var size = Synchsafe(head.AsSpan(6, 4));
                Add("ID3v2", checked(10 + size + (head[3] == 4 && (head[5] & 16) != 0 ? 10 : 0)));
            }
            if (input.Length >= 128)
            {
                input.Position = input.Length - 128;
                if (reader.ReadBytes(3).AsSpan().SequenceEqual("TAG"u8)) { input.Position -= 3; Add("ID3v1", 128); }
            }
            var tail = input.Length - (blocks.Any(b => b.Kind == "ID3v1") ? 128 : 0);
            if (tail >= 32)
            {
                input.Position = tail - 32; var footer = reader.ReadBytes(32);
                if (footer.AsSpan().StartsWith("APETAGEX"u8))
                {
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
                    if (size > tail || size < 32) throw new InvalidDataException("AudioShell.InvalidContainer");
                    var start = tail - size;
                    if (start >= 32) { input.Position = start - 32; if (reader.ReadBytes(8).AsSpan().SequenceEqual("APETAGEX"u8)) start -= 32; }
                    input.Position = start; Add("APEv2", checked((int)(tail - start)));
                }
            }
        }
        else throw new InvalidDataException("AudioShell.UnsupportedFile");
        return blocks;
    }
    internal static int Synchsafe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4 || bytes.ToArray().Any(b => b >= 128)) throw new InvalidDataException("AudioShell.InvalidContainer");
        return (bytes[0] << 21) | (bytes[1] << 14) | (bytes[2] << 7) | bytes[3];
    }
}
