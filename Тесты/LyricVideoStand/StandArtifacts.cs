using System.Buffers.Binary;
using System.IO;
using System.Text;
using AIHub.Services;

namespace LyricVideoStand
{
    internal static class StandArtifacts
    {
        public static void Png(string path, byte[] image, StandProvenance facts)
        {
            // Insert an uncompressed UTF-8 iTXt before IDAT; encoded pixels stay intact.
            if (image.Length < 33 || !image.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("Invalid PNG.");
            var body = Encoding.UTF8.GetBytes("LOPATA\0\0\0\0\0" + facts.Json());
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(image.AsSpan(0, 33));
            var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            output.Write(length); output.Write("iTXt"u8); output.Write(body);
            uint crc = uint.MaxValue;
            foreach (var b in "iTXt"u8.ToArray().Concat(body))
            {
                crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc); output.Write(length); output.Write(image.AsSpan(33)); output.Flush(true);
        }
        public static void VerifyPng(string path)
        {
            if (!PngTextMetadata.Read(path).ContainsKey("LOPATA")) throw new IOException("PNG provenance missing.");
        }
    }
}

namespace AIHub.Services
{
    // Pure formatting adapter required by the linked metadata reader's unused writer methods.
    // Kept identical to the app's FFMETADATA1 escaping; no input-tag writing is exposed by the stand.
    internal static class MusicSongMetadata
    {
        public static string FfMetadata(IReadOnlyDictionary<string, string> values)
        {
            var text = new StringBuilder(";FFMETADATA1\n");
            foreach (var item in values) text.Append(Escape(item.Key)).Append('=').Append(Escape(item.Value)).Append('\n');
            return text.ToString();
        }
        private static string Escape(string text)
        {
            if (text.Contains('\0')) throw new InvalidDataException("Audio tags cannot contain NUL characters.");
            return text.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\r", "\\\r").Replace("\n", "\\\n");
        }
    }
}
