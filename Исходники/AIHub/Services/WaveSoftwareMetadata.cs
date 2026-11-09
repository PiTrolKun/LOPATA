using System.Buffers.Binary;
using System.IO;

namespace AIHub.Services;

/// <summary>Projects the creator into RIFF INFO without changing any encoded audio bytes.</summary>
internal static class WaveSoftwareMetadata
{
    internal static void Write(string path, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".info";
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var header = new byte[12]; input.ReadExactly(header);
                if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8)
                    || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != input.Length - 8)
                    throw new InvalidDataException("Invalid PCM WAV.");
                output.Write(header);
                var chunk = new byte[8]; var buffer = new byte[65536];
                while (input.Position < input.Length)
                {
                    token.ThrowIfCancellationRequested(); input.ReadExactly(chunk);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                    var padded = checked((long)size + (size & 1));
                    if (padded > input.Length - input.Position) throw new InvalidDataException("Invalid WAV chunk.");
                    if (chunk.AsSpan(0, 4).SequenceEqual("LIST"u8) && size is >= 4 and <= 1024 * 1024)
                    {
                        var data = new byte[checked((int)padded)]; input.ReadExactly(data);
                        if (data.AsSpan(0, 4).SequenceEqual("INFO"u8)) WriteInfo(output, data.AsSpan(0, (int)size));
                        else { output.Write(chunk); output.Write(data); }
                    }
                    else
                    {
                        output.Write(chunk);
                        while (padded > 0)
                        {
                            token.ThrowIfCancellationRequested(); var read = (int)Math.Min(padded, buffer.Length);
                            input.ReadExactly(buffer.AsSpan(0, read)); output.Write(buffer, 0, read); padded -= read;
                        }
                    }
                }
                // A separate INFO list is valid RIFF. All former ISFT entries were removed above.
                output.Write("LIST\x14\0\0\0INFOISFT\x07\0\0\0LOPATA\0\0"u8);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)(output.Length - 8)));
                output.Position = 0; output.Write(header); output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteInfo(Stream output, ReadOnlySpan<byte> data)
    {
        using var kept = new MemoryStream(); kept.Write("INFO"u8);
        for (var offset = 4; offset < data.Length;)
        {
            if (data.Length - offset < 8) throw new InvalidDataException("Invalid RIFF INFO.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var total = checked(8L + size + (size & 1));
            if (total > data.Length - offset) throw new InvalidDataException("Invalid RIFF INFO.");
            if (!data.Slice(offset, 4).SequenceEqual("ISFT"u8)) kept.Write(data.Slice(offset, (int)total));
            offset += (int)total;
        }
        if (kept.Length == 4) return;
        Span<byte> header = stackalloc byte[8]; "LIST"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)kept.Length));
        output.Write(header); kept.Position = 0; kept.CopyTo(output);
        if ((kept.Length & 1) != 0) output.WriteByte(0);
    }
}
