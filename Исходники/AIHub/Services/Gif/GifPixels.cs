using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AIHub.Services;

public sealed record GifPixels(int Width, int Height, byte[] Bgra)
{
    public static GifPixels From(BitmapSource frame, int percent)
    {
        var pixels = ScreenshotFiles.Pixels(frame);
        return Resize(new(frame.PixelWidth, frame.PixelHeight, pixels),
            Math.Max(1, (int)Math.Round(frame.PixelWidth * percent / 100d)),
            Math.Max(1, (int)Math.Round(frame.PixelHeight * percent / 100d)));
    }
    public static GifPixels Resize(GifPixels input, int width, int height)
    {
        if (input.Width == width && input.Height == height) return input;
        using var bitmap = new SKBitmap(new SKImageInfo(input.Width, input.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(input.Bgra, 0, bitmap.GetPixels(), input.Bgra.Length);
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(bitmap))
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        var bytes = new byte[checked(width * height * 4)]; Marshal.Copy(output.GetPixels(), bytes, 0, bytes.Length);
        return new(width, height, bytes);
    }
}
