using System.IO;
using Lopata.Updates;
using SkiaSharp;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoReferenceFile(VideoReferenceImage Image, string Path);

/// <summary>Bounded local raster input; an imported original is copied byte-for-byte, never re-encoded.</summary>
public static class VideoReferenceFiles
{
    public const long MaxBytes = 128L * 1024 * 1024;
    public const long MaxPixels = 64_000_000;
    public static SKImageInfo Inspect(string path)
    {
        SafeUpdatePath.RejectLinks(Path.GetFullPath(path));
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("VideoPrep.RefMissingFile");
        if (file.Length is <= 0 or > MaxBytes) throw new InvalidDataException("VideoPrep.RefImageLimit");
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("VideoPrep.RefInvalidImage");
        var info = codec.Info;
        if (info.Width is < 1 or > 32768 || info.Height is < 1 or > 32768 || (long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException("VideoPrep.RefImageLimit");
        return codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
            ? new SKImageInfo(info.Height, info.Width, info.ColorType, info.AlphaType) : info;
    }
    public static async Task<VideoReferenceFile> ImportAsync(string source, string directory, CancellationToken token = default)
    {
        source = Path.GetFullPath(source); directory = Path.GetFullPath(directory);
        var name = Path.GetFileName(source);
        if (name.Length > 1024 || name.Any(char.IsControl)) throw new InvalidDataException("VideoPrep.RefInvalidImage");
        SafeUpdatePath.RejectLinks(source); SafeUpdatePath.RejectLinks(directory);
        token.ThrowIfCancellationRequested();
        // Hold the source read-only throughout inspection and copy, blocking writes/deletes on Windows.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var info = Inspect(source);
        using var codec = SKCodec.Create(source) ?? throw new InvalidDataException("VideoPrep.RefInvalidImage");
        var extension = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ".png", SKEncodedImageFormat.Jpeg => ".jpg", SKEncodedImageFormat.Webp => ".webp",
            SKEncodedImageFormat.Bmp => ".bmp", SKEncodedImageFormat.Gif => ".gif", _ => throw new InvalidDataException("VideoPrep.RefInvalidImage")
        };
        var id = Guid.NewGuid().ToString("N"); var destination = Path.Combine(directory, id + extension);
        Directory.CreateDirectory(directory); SafeUpdatePath.RejectLinks(directory);
        var created = false;
        try
        {
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                created = true; await input.CopyToAsync(output, token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            // Decode once before accepting an image with a valid header but broken pixel data.
            using var preview = Preview(destination); token.ThrowIfCancellationRequested();
            return new(new() { Id = id, Name = name, Width = info.Width, Height = info.Height,
                AttachmentRole = "video-reference/" + id }, destination);
        }
        catch { if (created && File.Exists(destination)) File.Delete(destination); throw; }
    }
    public static SKBitmap Preview(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Inspect(path);
        try
        {
            var thumbnail = FileMetadataPreview.Thumbnail(File.ReadAllBytes(path), MaxBytes);
            using var decoded = SKBitmap.Decode(thumbnail) ?? throw new InvalidDataException("VideoPrep.RefInvalidImage");
            var result = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (decoded.ScalePixels(result, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return result;
            result.Dispose(); throw new InvalidDataException("VideoPrep.RefInvalidImage");
        }
        catch (InvalidDataException error) { throw new InvalidDataException("VideoPrep.RefInvalidImage", error); }
    }
}
