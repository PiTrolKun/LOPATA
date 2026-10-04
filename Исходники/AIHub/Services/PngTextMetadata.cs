using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Reads uncompressed text from LOPATA PNGs without decoding or loading their IDAT.</summary>
public static class PngTextMetadata
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8]; file.ReadExactly(header);
        if (!header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Invalid PNG signature.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var utf8 = new UTF8Encoding(false, true);
        while (file.Position < file.Length)
        {
            file.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = Encoding.ASCII.GetString(header[4..]);
            if ((long)length + 4 > file.Length - file.Position) throw new InvalidDataException("Truncated PNG chunk.");
            if (type is "iTXt" or "tEXt" && length <= 4 * 1024 * 1024)
            {
                var data = new byte[(int)length]; file.ReadExactly(data);
                var end = Array.IndexOf(data, (byte)0);
                if (end > 0)
                {
                    var key = Encoding.Latin1.GetString(data, 0, end);
                    if (type == "tEXt") fields[key] = Encoding.Latin1.GetString(data, end + 1, data.Length - end - 1);
                    else if (end + 3 < data.Length && data[end + 1] == 0 && data[end + 2] == 0)
                    {
                        var languageEnd = Array.IndexOf(data, (byte)0, end + 3);
                        var translatedEnd = languageEnd < 0 ? -1 : Array.IndexOf(data, (byte)0, languageEnd + 1);
                        if (translatedEnd >= 0) fields[key] = utf8.GetString(data, translatedEnd + 1, data.Length - translatedEnd - 1);
                    }
                }
            }
            else file.Seek(length, SeekOrigin.Current);
            file.Seek(4, SeekOrigin.Current);
            if (type == "IEND") break;
        }
        return fields;
    }
}
