using System.IO;
using AIHub.Models;
using SkiaSharp;

namespace AIHub.Services;

/// <summary>CPU-only raster resizing, usable by generation and future image utilities.</summary>
public static class ImageRasterScaler
{
    public static string Algorithm(int sourceWidth, int sourceHeight, int width, int height) =>
        sourceWidth == width && sourceHeight == height ? "None" :
        sourceWidth > width * 2 || sourceHeight > height * 2 ? "Mitchell (staged reduction)" : "Mitchell";

    public static void SavePng(string source, string destination, int width, int height,
        IReadOnlyDictionary<string, string> metadata, CancellationToken token)
        => Save(source, destination, width, height, ImageGenerationFileFormat.Png, metadata, token);

    internal static void Save(string source, string destination, int width, int height, ImageGenerationFileFormat format,
        IReadOnlyDictionary<string, string> metadata, CancellationToken token)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source and destination must differ.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(source);
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".resize.tmp";
        SKBitmap? pixels = null;
        try
        {
            token.ThrowIfCancellationRequested();
            pixels = SKBitmap.Decode(source) ?? throw new InvalidDataException("Generation.InvalidImage");
            if (format == ImageGenerationFileFormat.Png && pixels.Width == width && pixels.Height == height)
                File.Copy(source, temporary, false); // Preserve compressed pixels when no resize is needed.
            else
            {
                while (pixels.Width > width * 2 || pixels.Height > height * 2)
                {
                    token.ThrowIfCancellationRequested();
                    var next = Resize(pixels, Math.Max(width, (pixels.Width + 1) / 2), Math.Max(height, (pixels.Height + 1) / 2));
                    pixels.Dispose(); pixels = next;
                }
                token.ThrowIfCancellationRequested();
                using var resized = pixels.Width == width && pixels.Height == height ? null : Resize(pixels, width, height);
                using var data = Encode(resized ?? pixels, format) ?? throw new IOException("Image encoding failed.");
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
                data.SaveTo(file); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (format == ImageGenerationFileFormat.Png)
                PngMetadataWriter.Write(temporary, metadata, token);
            else
            {
                if (!metadata.TryGetValue("XML:com.adobe.xmp", out var xmp))
                    throw new InvalidDataException("Generation.MetadataRequired");
                OutputImageMetadata.WriteXmp(temporary, ImageGenerationOutput.FormatId(format), width, height, xmp, token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            pixels?.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SKData? Encode(SKBitmap pixels, ImageGenerationFileFormat format)
    {
        if (format == ImageGenerationFileFormat.WebP)
        {
            using var pixmap = pixels.PeekPixels();
            return pixmap?.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 70));
        }
        using var image = SKImage.FromBitmap(pixels);
        if (format == ImageGenerationFileFormat.Png) return image.Encode(SKEncodedImageFormat.Png, 100);

        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, pixels.ColorSpace);
        using var surface = SKSurface.Create(info) ?? throw new IOException("JPEG raster allocation failed.");
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, 0, 0);
        using var opaque = surface.Snapshot();
        return opaque.Encode(SKEncodedImageFormat.Jpeg, 95);
    }

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace);
        using var surface = SKSurface.Create(info) ?? throw new IOException("Raster allocation failed.");
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot) ?? throw new IOException("Raster readback failed.");
    }
}
