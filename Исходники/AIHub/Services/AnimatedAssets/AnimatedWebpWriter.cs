using System.Buffers.Binary;
using System.IO;
using SkiaSharp;

namespace AIHub.Services.AnimatedAssets;

/// <summary>Wraps Skia's lossless still frames in the extended animated WebP container.</summary>
internal static class AnimatedWebpWriter
{
    public static void Write(Stream output, SKSizeI size, IEnumerable<SKBitmap> frames, int millisecondsPerFrame,
        CancellationToken token)
    {
        output.Write("RIFF"u8);
        WriteUInt32(output, 0);
        output.Write("WEBP"u8);
        output.Write("VP8X"u8);
        WriteUInt32(output, 10);
        var featuresPosition = output.Position;
        output.Write([2, 0, 0, 0]); // Animation is present. Alpha is recorded from the encoded frames below.
        WriteUInt24(output, size.Width - 1);
        WriteUInt24(output, size.Height - 1);
        output.Write("ANIM"u8);
        WriteUInt32(output, 6);
        output.Write([0, 0, 0, 0, 0, 0]); // Transparent background and infinite loop.

        byte[]? previous = null;
        var duration = 0;
        var alpha = false;
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            var data = EncodeFrame(frame, out var frameHasAlpha);
            alpha |= frameHasAlpha;
            if (previous is not null && !data.AsSpan().SequenceEqual(previous))
            {
                WriteAnimationFrame(output, size, previous, duration);
                duration = 0;
            }
            previous = data;
            duration += millisecondsPerFrame;
        }

        if (previous is null)
            throw new InvalidDataException("AnimatedAssets.Error.Encode");
        WriteAnimationFrame(output, size, previous, duration);
        var end = output.Position;
        output.Position = featuresPosition;
        output.WriteByte(alpha ? (byte)18 : (byte)2);
        output.Position = 4;
        WriteUInt32(output, checked((uint)(end - 8)));
        output.Position = end;
    }

    private static byte[] EncodeFrame(SKBitmap frame, out bool alpha)
    {
        using var pixmap = frame.PeekPixels();
        using var data = pixmap?.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 45))
            ?? throw new InvalidDataException("AnimatedAssets.Error.Encode");
        var riff = data.AsSpan();
        if (riff.Length < 25 || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WEBP"u8))
            throw new InvalidDataException("AnimatedAssets.Error.Encode");
        // A still WebP can carry optional standard chunks. Only its lossless image data belongs in an ANMF.
        for (var offset = 12; offset <= riff.Length - 8;)
        {
            var chunk = riff[offset..];
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]));
            var size = checked(8 + length + (length & 1));
            if (size > chunk.Length)
                break;
            if (length >= 5 && chunk[..4].SequenceEqual("VP8L"u8))
            {
                alpha = (chunk[12] & 16) != 0;
                return chunk[..size].ToArray();
            }
            offset += size;
        }
        throw new InvalidDataException("AnimatedAssets.Error.Encode");
    }

    private static void WriteAnimationFrame(Stream output, SKSizeI size, byte[] image, int duration)
    {
        output.Write("ANMF"u8);
        WriteUInt32(output, checked((uint)(16 + image.Length)));
        output.Write([0, 0, 0, 0, 0, 0]); // Full-canvas frame at (0, 0).
        WriteUInt24(output, size.Width - 1);
        WriteUInt24(output, size.Height - 1);
        WriteUInt24(output, duration);
        output.WriteByte(2); // Replace the region, including transparent pixels.
        output.Write(image);
    }

    private static void WriteUInt32(Stream output, uint number)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, number);
        output.Write(bytes);
    }

    private static void WriteUInt24(Stream output, int number) =>
        output.Write([(byte)number, (byte)(number >> 8), (byte)(number >> 16)]);
}
