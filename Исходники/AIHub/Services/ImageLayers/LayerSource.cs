using System.Drawing;
using System.IO;
using SkiaSharp;

namespace AIHub.Services.ImageLayers;

internal sealed record LayerSource(string Name, byte[] Original, int Width, int Height, byte[] Pixels)
{
    internal const int MaxInputBytes = 128 * 1024 * 1024;
    internal const int MaxPixels = 40_000_000;
    internal Rectangle Frame => new(0, 0, Width, Height);

    internal LayerMask VisibleMask(CancellationToken token) => LayerMask.Build(Width * Height, bits =>
    {
        for (var row = 0; row < Height; row++)
        {
            token.ThrowIfCancellationRequested();
            for (var index = row * Width; index < (row + 1) * Width; index++)
                bits[index] = Pixels[index * 4 + 3] != 0;
        }
    });

    internal static LayerSource Load(string name, byte[] input)
    {
        if (input.Length is 0 or > MaxInputBytes) throw new InvalidDataException("ImageLayers.InvalidImage");
        using var data = SKData.CreateCopy(input);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.FrameCount > 1 || codec.Info.Width < 2 || codec.Info.Height < 2
            || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
            throw new InvalidDataException("ImageLayers.InvalidImage");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("ImageLayers.InvalidImage");
        var origin = (int)codec.EncodedOrigin;
        var width = origin >= 5 ? info.Height : info.Width;
        var height = origin >= 5 ? info.Width : info.Height;
        var bytes = decoded.Bytes;
        if (origin == 1) return new(Path.GetFileName(name), input, width, height, bytes);
        var rotated = new byte[bytes.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (sx, sy) = origin switch
                {
                    2 => (info.Width - 1 - x, y), 3 => (info.Width - 1 - x, info.Height - 1 - y),
                    4 => (x, info.Height - 1 - y), 5 => (y, x), 6 => (y, info.Height - 1 - x),
                    7 => (info.Width - 1 - y, info.Height - 1 - x), 8 => (info.Width - 1 - y, x),
                    _ => (x, y)
                };
                Buffer.BlockCopy(bytes, (sy * info.Width + sx) * 4, rotated, (y * width + x) * 4, 4);
            }
        return new(Path.GetFileName(name), input, width, height, rotated);
    }

    internal byte[] Encode(Rectangle crop, bool forModel)
    {
        if (crop.Width < 1 || crop.Height < 1 || !Frame.Contains(crop))
            throw new ArgumentOutOfRangeException(nameof(crop));
        using var bitmap = new SKBitmap(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var row = 0; row < crop.Height; row++)
            System.Runtime.InteropServices.Marshal.Copy(Pixels, ((crop.Y + row) * Width + crop.X) * 4,
                bitmap.GetPixels() + row * bitmap.RowBytes, crop.Width * 4);
        if (!forModel) return Encode(bitmap, SKEncodedImageFormat.Png, 100);
        var scale = Math.Min(1, 1344d / Math.Max(crop.Width, crop.Height));
        using var jpegFrame = new SKBitmap(Math.Max(1, (int)Math.Round(crop.Width * scale)),
            Math.Max(1, (int)Math.Round(crop.Height * scale)), SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(jpegFrame);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawImage(image, new SKRect(0, 0, jpegFrame.Width, jpegFrame.Height), new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        return Encode(jpegFrame, SKEncodedImageFormat.Jpeg, 94);
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var encoded = bitmap.Encode(format, quality) ?? throw new InvalidDataException("ImageLayers.InvalidImage");
        return encoded.ToArray();
    }
}
