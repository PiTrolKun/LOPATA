using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Artist/title identity comes from input fields, never from numbered output filenames.</summary>
public sealed class MusicCoverStore(string root)
{
    public static MusicCoverStore Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Covers"));
    private readonly object _gate = new();
    public static string Normalize(string text) => text.Trim().Normalize(NormalizationForm.FormC);
    public static string Key(string artist, string title) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new[] { Normalize(artist), Normalize(title) })));
    private string Folder(string artist, string title) => Path.Combine(root, Key(artist, title));
    public string RunFolder(MusicCoverInput input)
    {
        if (!Guid.TryParseExact(input.Id, "N", out _)) throw new InvalidDataException("Music.Cover.Invalid");
        return Path.Combine(Folder(input.Artist, input.Title), "Runs", input.Id);
    }
    public MusicCoverEntry Read(string artist, string title)
    {
        lock (_gate)
        {
            var path = Path.Combine(Folder(artist, title), "cover.json");
            if (!File.Exists(path)) return new(Normalize(artist), Normalize(title));
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            var entry = JsonSerializer.Deserialize<MusicCoverEntry>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Music.Cover.Invalid");
            if (entry.Artist != Normalize(artist) || entry.Title != Normalize(title)) throw new InvalidDataException("Music.Cover.Invalid");
            return entry;
        }
    }
    public void SetEnabled(string artist, string title, bool enabled)
    { lock (_gate) Save(Read(artist, title) with { Enabled = enabled }); }
    private void Save(MusicCoverEntry entry) => Atomic(Path.Combine(Folder(entry.Artist, entry.Title), "cover.json"), entry);
    public MusicCoverRun Create(MusicCoverInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Lyrics) || input.Lyrics.Length > 200000
            || input.Artist.Length > 160 || input.Title.Length > 160 || input.Style.Length > 32000
            || input.PromptOverride?.Length > 12000) throw new InvalidDataException("Music.Cover.InputRequired");
        var run = new MusicCoverRun(input with { Artist = Normalize(input.Artist), Title = Normalize(input.Title) });
        lock (_gate)
        {
            if (Directory.Exists(RunFolder(input))) throw new IOException("Cover run already exists.");
            SaveRun(run); Save(Read(input.Artist, input.Title) with { LastRunId = input.Id }); return run;
        }
    }
    public MusicCoverRun Load(MusicCoverInput input)
    {
        var path = Path.Combine(RunFolder(input), "run.json"); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var run = JsonSerializer.Deserialize<MusicCoverRun>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Music.Cover.Invalid");
        if (run.Input.Id != input.Id || Key(run.Input.Artist, run.Input.Title) != Key(input.Artist, input.Title))
            throw new InvalidDataException("Music.Cover.Invalid");
        return run;
    }
    public MusicCoverRun? Latest(string artist, string title)
    {
        var entry = Read(artist, title);
        if (entry.LastRunId is null) return null;
        return Load(new(entry.LastRunId, artist, title, "", "", "", ""));
    }
    public MusicCoverRun SaveRun(MusicCoverRun run)
    { Atomic(Path.Combine(RunFolder(run.Input), "run.json"), run); return run; }
    public MusicCoverSelection Confirm(MusicCoverInput input, string path)
    {
        var run = Load(input);
        if (!run.Ready.Contains(path, StringComparer.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(RunFolder(input)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Cover.Invalid");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var selection = new MusicCoverSelection(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "image/jpeg")
            { ModelName = ImageGenerationCatalog.Get(run.Images?.ModelId ?? "z-image").Name };
        _ = ReadArtwork(selection);
        lock (_gate) Save(Read(input.Artist, input.Title) with { Selected = selection, Enabled = true });
        return selection;
    }
    internal static AudioCover ReadArtwork(MusicCoverSelection selection)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(selection.Path);
        var file = new FileInfo(selection.Path);
        if (!file.Exists || file.Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Music.Cover.Invalid");
        var bytes = File.ReadAllBytes(selection.Path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(selection.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Cover.Invalid");
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(bytes));
        if (codec is null || codec.Info.Width != 1024 || codec.Info.Height != 1024 || selection.Mime != "image/jpeg"
            || codec.EncodedFormat != SkiaSharp.SKEncodedImageFormat.Jpeg) throw new InvalidDataException("Music.Cover.Invalid");
        return new(selection.Mime, 3, "LOPATA", bytes);
    }
    internal static void Atomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
