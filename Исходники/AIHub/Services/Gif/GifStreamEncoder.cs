using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Streaming GIF89a with a stable 256-colour palette and bounded LZW dictionary.
/// Keeps one indexed frame, never the entire animation. Original implementation.</summary>
public sealed class GifStreamEncoder : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly int _width, _height;
    private bool _finished;
    public GifStreamEncoder(Stream stream, int width, int height, bool loop)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width; _height = height; _writer = new(stream, Encoding.ASCII, leaveOpen: true);
        _writer.Write(Encoding.ASCII.GetBytes("GIF89a")); _writer.Write((ushort)width); _writer.Write((ushort)height);
        _writer.Write((byte)0xF7); _writer.Write((byte)0); _writer.Write((byte)0);
        for (var r = 0; r < 6; r++) for (var g = 0; g < 6; g++) for (var b = 0; b < 6; b++)
        { _writer.Write((byte)(r * 51)); _writer.Write((byte)(g * 51)); _writer.Write((byte)(b * 51)); }
        for (var i = 0; i < 40; i++) { var grey = (byte)Math.Round(i * 255d / 39); _writer.Write(grey); _writer.Write(grey); _writer.Write(grey); }
        if (loop)
        { _writer.Write(new byte[] { 0x21, 0xFF, 11 }); _writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0")); _writer.Write(new byte[] { 3, 1, 0, 0, 0 }); }
    }
    public static byte[] Index(GifPixels frame, CancellationToken token = default)
    {
        var result = new byte[checked(frame.Width * frame.Height)];
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var b = frame.Bgra[i * 4]; var g = frame.Bgra[i * 4 + 1]; var r = frame.Bgra[i * 4 + 2];
            result[i] = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 16
                ? (byte)(216 + (int)Math.Round((r + g + b) / 3d * 39 / 255))
                : (byte)(((r + 25) / 51) * 36 + ((g + 25) / 51) * 6 + (b + 25) / 51);
        }
        return result;
    }
    public void Add(byte[] indices, int centiseconds, CancellationToken token = default)
    {
        if (_finished || indices.Length != checked(_width * _height)) throw new InvalidOperationException("Invalid GIF frame.");
        _writer.Write(new byte[] { 0x21, 0xF9, 4, 4 }); // Keep previous image, no transparency.
        _writer.Write((ushort)Math.Clamp(centiseconds, 1, 65535)); _writer.Write((byte)0); _writer.Write((byte)0);
        _writer.Write((byte)0x2C); _writer.Write((ushort)0); _writer.Write((ushort)0);
        _writer.Write((ushort)_width); _writer.Write((ushort)_height); _writer.Write((byte)0); _writer.Write((byte)8);
        var block = new byte[255]; var count = 0; uint accumulator = 0; var pendingBits = 0; var codeBits = 9;
        void Byte(byte value) { block[count++] = value; if (count == 255) Flush(); }
        void Flush() { if (count == 0) return; _writer.Write((byte)count); _writer.Write(block, 0, count); count = 0; }
        void Code(int value)
        {
            accumulator |= (uint)value << pendingBits; pendingBits += codeBits;
            while (pendingBits >= 8) { Byte((byte)accumulator); accumulator >>= 8; pendingBits -= 8; }
        }
        var dictionary = new Dictionary<int, int>(4096); var next = 258; Code(256); var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var symbol = indices[i]; var key = (prefix << 8) | symbol;
            if (dictionary.TryGetValue(key, out var found)) { prefix = found; continue; }
            Code(prefix);
            if (next == (1 << codeBits) && codeBits < 12) codeBits++;
            if (next < 4096) dictionary[key] = next++;
            else { Code(256); dictionary.Clear(); next = 258; codeBits = 9; }
            prefix = symbol;
        }
        Code(prefix); if (next == (1 << codeBits) && codeBits < 12) codeBits++;
        Code(257); if (pendingBits > 0) Byte((byte)accumulator); Flush(); _writer.Write((byte)0);
    }
    public void Finish() { if (_finished) return; _writer.Write((byte)0x3B); _writer.Flush(); _finished = true; }
    public void Dispose() { _writer.Dispose(); }
}
