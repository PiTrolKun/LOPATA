using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicGenerationVariant(int LanguageSeed, int SoundSeed, string ResultPath)
{
    public string? PlanFile { get; init; }
    public string? PlanHash { get; init; }
    public string? AudioFile { get; init; }
    public string? AudioHash { get; init; }
    public bool Completed { get; init; }
}
public sealed record MusicGenerationJob(string Id, string ModelsRoot, string OutputFolder, string Title,
    string Style, string Lyrics, int DurationSeconds, DateTime CreatedAt, MusicGenerationVariant[] Variants)
{
    public string RuntimeRevision { get; init; } = MusicYueRuntime.Revision;
    public string ModelRevision { get; init; } = MusicComponentCatalog.Revision;
    public string RuntimePack { get; init; } = MusicYueRuntime.CpuPack;
    public MusicExpertSettings Expert { get; init; } = new();
}

public sealed class MusicGenerationJobs(string directory)
{
    public static MusicGenerationJobs Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Jobs"));
    public string Folder(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(directory, id)
        : throw new InvalidDataException("Invalid music job identifier.");
    public MusicGenerationJob Load(string id)
    {
        var job = JsonSerializer.Deserialize<MusicGenerationJob>(File.ReadAllText(Path.Combine(Folder(id), "job.json")))
            ?? throw new InvalidDataException("Missing music job.");
        Validate(job); if (job.Id != id) throw new InvalidDataException("Music job identity mismatch."); return job;
    }
    public void Save(MusicGenerationJob job)
    {
        Validate(job); var folder = Folder(job.Id); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "job.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, job); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, true);
    }
    public MusicGenerationJob Create(string modelsRoot, string output, string title, int count, int duration, string style, string lyrics,
        MusicGenerationVariant? repeat = null, MusicExpertSettings? expert = null)
    {
        if (count is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(count));
        var settings = (expert ?? new()).Snapshot(); settings.Validate();
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose an audio output folder.");
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var now = DateTime.Now; var name = CleanTitle(title);
        if (name.Length == 0) name = "Music_" + now.ToString("yyyy-MM-dd_HH-mm-ss");
        var variants = Enumerable.Range(0, count).Select(i => new MusicGenerationVariant(
            repeat?.LanguageSeed ?? Seed("lm_seed", i),
            repeat?.SoundSeed ?? Seed("seed", i),
            UniquePath(output, name + (count > 1 ? "_" + (i + 1).ToString("000") : "")))).ToArray();
        var job = new MusicGenerationJob(Guid.NewGuid().ToString("N"), Path.GetFullPath(modelsRoot), output, name, style, lyrics, duration, now, variants)
            { RuntimePack = Path.GetFileName(MusicYueRuntime.DirectoryPath), Expert = settings };
        Save(job); return job;
        int Seed(string key, int index) => settings.Integer(key) < 0 ? RandomNumberGenerator.GetInt32(1, int.MaxValue)
            : (int)(((long)settings.Integer(key) + index) % ((long)int.MaxValue + 1));
    }
    public string StagePath(string id, string suffix) => Path.Combine(Folder(id), Guid.NewGuid().ToString("N") + suffix);
    public IReadOnlyList<MusicTrack> Tracks(string id)
    {
        var job = Load(id);
        return job.Variants.Select((v, i) => (v, i)).Where(p => p.v.Completed && File.Exists(p.v.ResultPath))
            .Select(p => new MusicTrack(p.v.ResultPath, Path.GetFileName(p.v.ResultPath), MusicWaveFile.ReadDuration(p.v.ResultPath), job.CreatedAt)
            { JobId = job.Id, Variant = p.i }).ToArray();
    }
    private void Validate(MusicGenerationJob job)
    {
        if (job.Expert is null) throw new InvalidDataException("Missing music settings snapshot.");
        job.Expert.Validate();
        var folder = Path.GetFullPath(Folder(job.Id)) + Path.DirectorySeparatorChar;
        if (job.RuntimeRevision != MusicYueRuntime.Revision || job.ModelRevision != MusicComponentCatalog.Revision
            || job.RuntimePack is not (MusicYueRuntime.CpuPack or MusicYueRuntime.CudaPack)
            || job.Variants.Length is < 1 or > 8 || !Path.IsPathFullyQualified(job.ModelsRoot)
            || !Path.IsPathFullyQualified(job.OutputFolder) || job.DurationSeconds is < 1 or > 360)
            throw new InvalidDataException("Unsupported music job.");
        foreach (var v in job.Variants)
        {
            if (v.LanguageSeed < 0 || v.SoundSeed < 0 || Path.GetDirectoryName(Path.GetFullPath(v.ResultPath)) != job.OutputFolder)
                throw new InvalidDataException("Invalid music variant.");
            foreach (var path in new[] { v.PlanFile, v.AudioFile }.OfType<string>())
                if (!Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Music stage path escapes its job folder.");
        }
        if (job.Variants.Select(v => v.ResultPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != job.Variants.Length)
            throw new InvalidDataException("Duplicate music result path.");
    }
    private static string CleanTitle(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat(value.Trim().Take(100).Select(c => invalid.Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        if (name.Length > 0 && new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name;
    }
    private static string UniquePath(string folder, string name)
    {
        for (var i = 0; i < 100000; i++)
        { var path = Path.Combine(folder, name + (i == 0 ? "" : "_" + i) + ".wav"); if (!File.Exists(path)) return path; }
        throw new IOException("No free music result name.");
    }
}
