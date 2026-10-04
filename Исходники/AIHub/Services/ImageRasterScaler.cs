using System.IO;
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
            if (pixels.Width == width && pixels.Height == height)
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
                using var final = Resize(pixels, width, height);
                using var image = SKImage.FromBitmap(final);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("PNG encoding failed.");
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
                data.SaveTo(file); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (metadata.Count > 0) PngMetadataWriter.Write(temporary, metadata, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            pixels?.Dispose();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
