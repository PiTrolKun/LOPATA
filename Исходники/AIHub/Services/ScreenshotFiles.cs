using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services;

public static class ScreenshotFiles
{
    public static string Folder(ScreenCaptureSettings settings) => string.IsNullOrWhiteSpace(settings.Folder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LOPATA")
        : Path.GetFullPath(settings.Folder);

    public static BitmapSource Enlarge(BitmapSource image)
    {
        var scale = Math.Max(2, Math.Max(100d / image.PixelWidth, 100d / image.PixelHeight));
        var width = checked((int)Math.Ceiling(image.PixelWidth * scale)); var height = checked((int)Math.Ceiling(image.PixelHeight * scale));
        if ((long)width * height > 32_000_000) throw new InvalidOperationException("Area enlargement exceeds the 32 megapixel safety limit. The original can still be saved.");
        var bytes = Pixels(image);
        using var input = new SKBitmap(new SKImageInfo(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(bytes, 0, input.GetPixels(), bytes.Length);
        using var output = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var inputImage = SKImage.FromBitmap(input))
            canvas.DrawImage(inputImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        var scaled = new byte[width * height * 4]; Marshal.Copy(output.GetPixels(), scaled, 0, scaled.Length);
        // Mild unsharp mask, bounded and preserving alpha; it does not invent details.
        var sharp = (byte[])scaled.Clone();
        for (var y = 1; y < height - 1; y++) for (var x = 1; x < width - 1; x++) for (var c = 0; c < 3; c++)
        {
            var i = (y * width + x) * 4 + c;
            var average = (scaled[i - 4] + scaled[i + 4] + scaled[i - width * 4] + scaled[i + width * 4]) / 4d;
            sharp[i] = (byte)Math.Clamp(Math.Round(scaled[i] + (scaled[i] - average) * 0.15), 0, sharp[i - c + 3]);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, sharp, width * 4);
        result.Freeze(); return result;
    }

    public static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        converted.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes;
    }

    public static IReadOnlyList<string> Save(ScreenCaptureSettings settings, CaptureSource source, BitmapSource original, BitmapSource? scaled)
    {
        var paths = new List<string>(); var folder = Folder(settings); Directory.CreateDirectory(folder);
        var stem = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_" + source.ToString().ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..6];
        foreach (var (image, suffix) in scaled is null
                     ? new[] { (original, "original") } : new[] { (original, "original"), (scaled, "scaled") })
        {
            var extension = settings.ImageFormat == "jpeg" ? "jpg" : settings.ImageFormat;
            var path = Path.Combine(folder, stem + "_" + suffix + "." + extension);
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (settings.ImageFormat == "webp")
                    {
                        var bytes = Pixels(image);
                        using var bitmap = new SKBitmap(new SKImageInfo(image.PixelWidth, image.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
                        using var pixmap = bitmap.PeekPixels();
                        using var data = pixmap.Encode(new SKWebpEncoderOptions(
                            settings.WebPLossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, settings.ImageQuality))
                            ?? throw new IOException("WebP encoding failed.");
                        data.SaveTo(stream);
                    }
                    else
                    {
                        BitmapEncoder encoder = settings.ImageFormat == "jpeg"
                            ? new JpegBitmapEncoder { QualityLevel = settings.ImageQuality } : new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
                    }
                    stream.Flush(true);
                }
                File.Move(temporary, path); paths.Add(path);
            }
            catch (Exception error) { throw new IOException("Screenshot saving failed: " + error.Message + ". Preserved: " + string.Join("; ", paths), error); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return paths;
    }
}
