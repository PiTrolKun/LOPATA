using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Atomically inserts UTF-8 iTXt chunks, preserving compressed pixels verbatim.</summary>
public static class PngMetadataWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        return crc;
    }).ToArray();

    public static void Write(string path, IReadOnlyDictionary<string, string> fields, CancellationToken token = default)
    {
        if (fields.Count == 0) return;
        foreach (var key in fields.Keys)
            if (key.Length is < 1 or > 79 || key.Any(c => c < 32 || c > 126)) throw new ArgumentException("Invalid PNG metadata keyword.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".metadata.tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                Copy(input, output, fields, token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Copy(Stream input, Stream output, IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        input.ReadExactly(header);
        if (!header.SequenceEqual(Signature)) throw new InvalidDataException("Invalid PNG signature.");
        output.Write(header);
        var buffer = new byte[65536];
        Span<byte> checksum = stackalloc byte[4];
        var first = true; var inserted = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header[4..]);
            if (length > int.MaxValue || length > input.Length - input.Position - 4
                || (first && (type != "IHDR" || length != 13))) throw new InvalidDataException("Invalid PNG chunk.");
            first = false;
            if (type == "IDAT" && !inserted)
            {
                foreach (var entry in fields) WriteText(output, entry.Key, entry.Value);
                inserted = true;
            }
            var prefixLength = type is "iTXt" or "tEXt" or "zTXt" ? (int)Math.Min(length, 80) : 0;
            input.ReadExactly(buffer.AsSpan(0, prefixLength));
            var separator = Array.IndexOf(buffer, (byte)0, 0, prefixLength);
            var replace = separator > 0 && fields.ContainsKey(Encoding.Latin1.GetString(buffer, 0, separator));
            var crc = Accumulate(uint.MaxValue, header[4..]);
            crc = Accumulate(crc, buffer.AsSpan(0, prefixLength));
            if (!replace) { output.Write(header); output.Write(buffer, 0, prefixLength); }
            long remaining = length - prefixLength;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                input.ReadExactly(buffer.AsSpan(0, count));
                crc = Accumulate(crc, buffer.AsSpan(0, count));
                if (!replace) output.Write(buffer, 0, count);
                remaining -= count;
            }
            input.ReadExactly(checksum);
            if (BinaryPrimitives.ReadUInt32BigEndian(checksum) != (crc ^ uint.MaxValue)) throw new InvalidDataException("Invalid PNG checksum.");
            if (!replace) output.Write(checksum);
            if (type != "IEND") continue;
            if (length != 0 || !inserted || input.Position != input.Length) throw new InvalidDataException("Invalid PNG end.");
            break;
        }
    }

    private static void WriteText(Stream output, string keyword, string value)
    {
        // Keyword, compression flag/method, language and translated keyword are NUL-delimited.
        var data = Encoding.UTF8.GetBytes(keyword + "\0\0\0\0\0" + value);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        "iTXt"u8.CopyTo(header[4..]);
        output.Write(header); output.Write(data);
        Span<byte> checksum = stackalloc byte[4];
        var crc = Accumulate(Accumulate(uint.MaxValue, header[4..]), data) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc); output.Write(checksum);
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }
}
