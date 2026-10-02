using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AIHub.Services;

public sealed record GifFrameEntry(string Name, double Milliseconds, int Width, int Height);
public sealed class GifManifest
{
    public int Version { get; set; } = 1;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Loop { get; set; }
    public double DurationMilliseconds { get; set; }
    public int MissedFrames { get; set; }
    public bool ReducedDetail { get; set; }
    public string Error { get; set; } = "";
    public List<GifFrameEntry> Frames { get; set; } = [];
}

/// <summary>Durable compressed CPU frames. The journal is replaced only after each frame is flushed.</summary>
public sealed class GifFrameStore
{
    public string DirectoryPath { get; }
    public GifManifest Manifest { get; }
    public GifFrameStore(string directory, bool loop)
    { DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath); Manifest = new() { Loop = loop }; SaveManifest(); }
    private GifFrameStore(string directory, GifManifest manifest) { DirectoryPath = directory; Manifest = manifest; }
    public static GifFrameStore Open(string directory)
    {
        var path = Path.GetFullPath(directory);
        var manifest = JsonSerializer.Deserialize<GifManifest>(File.ReadAllText(Path.Combine(path, "recording.json"))) ?? throw new IOException("Invalid recording journal.");
        if (manifest.Version != 1 || manifest.Width <= 0 || manifest.Height <= 0 || (long)manifest.Width * manifest.Height > 64_000_000 || manifest.Frames.Count > 2000)
            throw new IOException("Invalid recording dimensions or frame count.");
        return new(path, manifest);
    }
    public void Append(GifPixels pixels, double milliseconds)
    {
        if (Manifest.Frames.Count == 0) { Manifest.Width = pixels.Width; Manifest.Height = pixels.Height; }
        var name = Manifest.Frames.Count.ToString("D5") + ".frame"; var path = Path.Combine(DirectoryPath, name);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { using (var compressed = new DeflateStream(file, CompressionLevel.Fastest, leaveOpen: true)) compressed.Write(pixels.Bgra); file.Flush(true); }
        Manifest.Frames.Add(new(name, milliseconds, pixels.Width, pixels.Height));
        Manifest.DurationMilliseconds = milliseconds; SaveManifest();
    }
    public GifPixels Read(GifFrameEntry entry)
    {
        if (entry.Name != Path.GetFileName(entry.Name) || !entry.Name.EndsWith(".frame", StringComparison.Ordinal) ||
            entry.Width <= 0 || entry.Height <= 0 || (long)entry.Width * entry.Height > 64_000_000) throw new IOException("Invalid cached frame.");
        using var file = File.OpenRead(Path.Combine(DirectoryPath, entry.Name)); using var compressed = new DeflateStream(file, CompressionMode.Decompress);
        var bytes = new byte[checked(entry.Width * entry.Height * 4)]; compressed.ReadExactly(bytes);
        return new(entry.Width, entry.Height, bytes);
    }
    public void SaveManifest()
    {
        var path = Path.Combine(DirectoryPath, "recording.json"); var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, Manifest); file.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
    public string Assemble(string destination, CancellationToken token, Action<int>? progress = null)
    {
        if (Manifest.Frames.Count == 0) throw new IOException("No usable recording frames were received.");
        var temporary = destination + ".tmp";
        // Unique output name; never overwrite an earlier file on retry.
        if (File.Exists(destination) || File.Exists(temporary)) throw new IOException("Output already exists.");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var encoder = new GifStreamEncoder(file, Manifest.Width, Manifest.Height, Manifest.Loop);
                byte[]? previous = null; var emittedCs = 0;
                for (var i = 0; i < Manifest.Frames.Count; i++)
                {
                    token.ThrowIfCancellationRequested(); var entry = Manifest.Frames[i];
                    var pixels = GifPixels.Resize(Read(entry), Manifest.Width, Manifest.Height);
                    var indexed = GifStreamEncoder.Index(pixels, token);
                    if (previous is null) previous = indexed;
                    else if (!previous.AsSpan().SequenceEqual(indexed))
                    {
                        var boundaryCs = (int)Math.Round((entry.Milliseconds - Manifest.Frames[0].Milliseconds) / 10);
                        encoder.Add(previous, Math.Max(1, boundaryCs - emittedCs), token); emittedCs = Math.Max(emittedCs + 1, boundaryCs);
                        previous = indexed;
                    }
                    progress?.Invoke((i + 1) * 100 / Manifest.Frames.Count);
                }
                var totalCs = (int)Math.Round(Math.Max(10, Manifest.DurationMilliseconds - Manifest.Frames[0].Milliseconds) / 10);
                encoder.Add(previous!, Math.Max(1, totalCs - emittedCs), token); encoder.Finish(); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
