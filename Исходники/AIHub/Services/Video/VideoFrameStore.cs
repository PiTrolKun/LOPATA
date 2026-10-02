using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using SkiaSharp;
using AIHub.Models;

namespace AIHub.Services;

public sealed record VideoFrameEntry(string Name, double At, int Width, int Height);
public sealed record VideoAudioTrack(string Name, string Format, int Rate, int Channels, int BlockAlign);
public sealed class VideoManifest
{
    public int Version { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
    public int MinFrameWidth { get; set; }
    public int MinFrameHeight { get; set; }
    public double DurationMilliseconds { get; set; }
    public int MissedFrames { get; set; }
    public bool ReducedDetail { get; set; }
    public string Error { get; set; } = "";
    public ScreenCaptureSettings Settings { get; set; } = new();
    public List<VideoAudioTrack> Audio { get; set; } = [];
}

/// <summary>Append-only timing journal, independently committed JPEG frames and raw audio.
/// Memory and journal writes stay bounded for long recordings. Unknown files are never removed.</summary>
public sealed class VideoFrameStore
{
    public string DirectoryPath { get; }
    public VideoManifest Manifest { get; }
    private int _count;
    private byte[]? _previous;
    private string? _previousName;
    private GifPixels? _previousPixels;
    public VideoFrameStore(string directory, ScreenCaptureSettings settings)
    {
        DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath);
        Manifest = new() { Settings = settings }; Save();
    }
    private VideoFrameStore(string directory, VideoManifest manifest) { DirectoryPath = directory; Manifest = manifest; }
    public static VideoFrameStore Open(string directory)
    {
        var path = Path.GetFullPath(directory);
        var manifest = JsonSerializer.Deserialize<VideoManifest>(File.ReadAllText(Path.Combine(path, "video.json"))) ?? throw new IOException("Invalid video journal.");
        if (manifest.Version != 1 || manifest.Width < 2 || manifest.Height < 2 || (long)manifest.Width * manifest.Height > 64_000_000)
            throw new IOException("Invalid video dimensions.");
        if (!double.IsFinite(manifest.DurationMilliseconds) || manifest.DurationMilliseconds < 0 || manifest.DurationMilliseconds > TimeSpan.MaxValue.TotalMilliseconds || manifest.Audio.Count > 2)
            throw new IOException("Invalid recording duration or audio tracks.");
        foreach (var track in manifest.Audio)
        {
            SafeName(track.Name, ".pcm");
            if (track.Format is not ("s16le" or "s24le" or "s32le" or "f32le") || track.Rate < 8000 || track.Rate > 384000 || track.Channels < 1 || track.Channels > 32 || track.BlockAlign != track.Channels * (track.Format == "s16le" ? 2 : track.Format == "s24le" ? 3 : 4))
                throw new IOException("Invalid audio track format.");
        }
        manifest.Settings.Normalize(); return new(path, manifest);
    }
    public static (int Width, int Height) OutputSize(int width, int height, string quality)
    {
        if (width < 2 || height < 2 || (long)width * height > 64_000_000) throw new IOException("Unsupported video dimensions.");
        var shortSide = quality switch { "720p" => 720, "1080p" => 1080, "1440p" => 1440, "4K" => 2160, _ => Math.Min(width, height) };
        // Quality presets never enlarge the source. Even dimensions are required by YUV 4:2:0 encoders.
        var ratio = Math.Min(1d, shortSide / (double)Math.Min(width, height));
        return (Math.Max(2, (int)Math.Round(width * ratio / 2) * 2), Math.Max(2, (int)Math.Round(height * ratio / 2) * 2));
    }
    public void Append(GifPixels pixels, double at)
    {
        if (_count == 0)
        { if (Manifest.Width == 0) (Manifest.Width, Manifest.Height) = OutputSize(pixels.Width, pixels.Height, Manifest.Settings.VideoQuality); Save(); }
        Manifest.MinFrameWidth = Manifest.MinFrameWidth == 0 ? pixels.Width : Math.Min(Manifest.MinFrameWidth, pixels.Width);
        Manifest.MinFrameHeight = Manifest.MinFrameHeight == 0 ? pixels.Height : Math.Min(Manifest.MinFrameHeight, pixels.Height);
        string name;
        if (_previousPixels is not null && _previousPixels.Width == pixels.Width && _previousPixels.Height == pixels.Height &&
            (ReferenceEquals(_previousPixels, pixels) || _previousPixels.Bgra.AsSpan().SequenceEqual(pixels.Bgra))) name = _previousName!;
        else
        {
            using var bitmap = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            Marshal.Copy(pixels.Bgra, 0, bitmap.GetPixels(), pixels.Bgra.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, Manifest.Settings.VideoCompression == 100 ? 100 : 92);
            var bytes = encoded.ToArray();
            if (_previous is not null && _previous.AsSpan().SequenceEqual(bytes)) name = _previousName!;
            else
            {
                name = _count.ToString("D10") + ".jpg";
                using var file = new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                file.Write(bytes); file.Flush(true); _previous = bytes; _previousName = name;
            }
        }
        _previousPixels = pixels;
        using (var journal = new FileStream(Path.Combine(DirectoryPath, "frames.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        { var line = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new VideoFrameEntry(name, at, pixels.Width, pixels.Height)) + "\n"); journal.Write(line); journal.Flush(true); }
        _count++; Manifest.DurationMilliseconds = at;
    }
    public IEnumerable<VideoFrameEntry> Frames(bool checkFiles = true)
    {
        double last = -1;
        using var reader = File.OpenText(Path.Combine(DirectoryPath, "frames.jsonl"));
        while (reader.ReadLine() is { } line)
        {
            VideoFrameEntry? entry;
            try { entry = JsonSerializer.Deserialize<VideoFrameEntry>(line); }
            catch (JsonException) when (reader.EndOfStream) { break; } // Only a last interrupted append is ignored.
            if (entry is null || !double.IsFinite(entry.At) || entry.At < last || entry.Width < 1 || entry.Height < 1 || (long)entry.Width * entry.Height > 64_000_000)
                throw new IOException("Invalid video frame timestamp or dimensions.");
            SafeName(entry.Name, ".jpg"); if (checkFiles && !File.Exists(Path.Combine(DirectoryPath, entry.Name))) throw new IOException("A cached video frame is missing.");
            last = entry.At; yield return entry;
        }
    }
    public GifPixels Read(VideoFrameEntry entry)
    {
        SafeName(entry.Name, ".jpg");
        using var bitmap = SKBitmap.Decode(Path.Combine(DirectoryPath, entry.Name)) ?? throw new IOException("Invalid cached video frame.");
        if (bitmap.Width != entry.Width || bitmap.Height != entry.Height) throw new IOException("Cached frame size differs from its journal.");
        using var bgra = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bgra)) canvas.DrawBitmap(bitmap, 0, 0);
        var bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)]; Marshal.Copy(bgra.GetPixels(), bytes, 0, bytes.Length);
        return new(bitmap.Width, bitmap.Height, bytes);
    }
    public static void SafeName(string name, string extension)
    { if (name != Path.GetFileName(name) || !name.EndsWith(extension, StringComparison.Ordinal) || name.Contains(':')) throw new IOException("Invalid cache filename."); }
    public void Save()
    {
        lock (this)
        {
        var path = Path.Combine(DirectoryPath, "video.json");
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, Manifest); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
        }
    }
    public void Cleanup()
    {
        try
        {
            // Stream cleanup: no unbounded collection of filenames.
            foreach (var frame in Frames(false)) File.Delete(Path.Combine(DirectoryPath, frame.Name));
            foreach (var track in Manifest.Audio) { SafeName(track.Name, ".pcm"); File.Delete(Path.Combine(DirectoryPath, track.Name)); }
            File.Delete(Path.Combine(DirectoryPath, "frames.jsonl")); File.Delete(Path.Combine(DirectoryPath, "video.json"));
            Directory.Delete(DirectoryPath);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
