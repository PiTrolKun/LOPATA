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
    public string? ResultHash { get; init; }
    public string? AdditionalPath { get; init; }
    public string? AdditionalHash { get; init; }
    public double? DurationSeconds { get; init; }
    public double? GenerationSeconds { get; init; }
    public string? Hardware { get; init; }
    public string? PlanHardware { get; init; }
    public string? UsedRuntimePack { get; init; }
    public string? ExecutionReceipt { get; init; }
    public bool Completed { get; init; }
}
public sealed record MusicGenerationJob(string Id, string ModelsRoot, string OutputFolder, string Title,
    string Style, string Lyrics, int DurationSeconds, DateTime CreatedAt, MusicGenerationVariant[] Variants)
{
    public int Schema { get; init; } = 1;
    public string Variation { get; init; } = MusicComponentCatalog.ModelId;
    public string DecoderRevision { get; init; } = MusicComponentCatalog.Revision;
    public string RuntimeRevision { get; init; } = MusicYueRuntime.Revision;
    public string ModelRevision { get; init; } = MusicComponentCatalog.Revision;
    public string RuntimePack { get; init; } = MusicYueRuntime.CpuPack;
    public MusicExpertSettings Expert { get; init; } = new();
    public MusicWishSnapshot? Wishes { get; init; }
    // Null means the original WAV-only job contract, including recovery of old jobs.
    public MusicOutputSettings? Output { get; init; }
    public string Artist { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool DurationAutomatic { get; init; }
    public string AppVersion { get; init; } = MusicSongMetadata.AppVersion;
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public int? ProjectStep { get; init; }
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
        if (File.Exists(path) && !File.Exists(path + ".schema1.bak") &&
            JsonSerializer.Deserialize<MusicGenerationJob>(File.ReadAllText(path))?.Schema == 1) File.Copy(path, path + ".schema1.bak");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, job with { Schema = 2 }); file.Flush(flushToDisk: true); }
        File.Move(temporary, path, true);
    }
    public MusicGenerationJob Create(string modelsRoot, string output, string title, int count, int duration, string style, string lyrics,
        MusicGenerationVariant? repeat = null, MusicExpertSettings? expert = null, MusicWishSnapshot? wishes = null,
        MusicOutputSettings? outputSettings = null, string artist = "", string comment = "", bool durationAutomatic = false)
    {
        if (count is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(count));
        var settings = (expert ?? new()).Snapshot(); settings.Validate();
        outputSettings?.Validate();
        if (MusicHeartMuLaCatalog.IsHeart(settings.Variation) && durationAutomatic) duration = settings.Integer("max_duration");
        if (MusicDiffRhythmCatalog.IsDiff(settings.Variation)) {
            if (durationAutomatic) duration = MusicDiffRhythmCatalog.MaximumDuration;
            if (duration > MusicDiffRhythmCatalog.MaximumDuration) throw new InvalidDataException("DiffRhythm duration exceeds 240 seconds.");
        }
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Choose an audio output folder.");
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var now = DateTime.Now; var name = CleanTitle(string.Join(" — ", new[] { artist.Trim(), title.Trim() }.Where(s => s.Length != 0)));
        if (name.Length == 0) name = "Music_" + now.ToString("yyyy-MM-dd_HH-mm-ss");
        var variants = Enumerable.Range(0, count).Select(i => new MusicGenerationVariant(
            repeat?.LanguageSeed ?? Seed("lm_seed", i),
            repeat?.SoundSeed ?? Seed("seed", i),
            UniquePath(output, name + (count > 1 ? "_" + (i + 1).ToString("000") : ""), outputSettings))).ToArray();
        if (outputSettings?.AdditionalFormat is { } extra) variants = variants.Select(v => v with {
            AdditionalPath = Path.ChangeExtension(v.ResultPath, MusicOutputSettings.Extension(extra)) }).ToArray();
        var job = new MusicGenerationJob(Guid.NewGuid().ToString("N"), Path.GetFullPath(modelsRoot), output,
            outputSettings is null && string.IsNullOrWhiteSpace(title) ? name : title.Trim(), style, lyrics, duration, now, variants)
            { Schema = 2, Variation = settings.Variation, ModelRevision = MusicModelVariants.Revision(settings.Variation),
                DecoderRevision = MusicHeartMuLaCatalog.IsHeart(settings.Variation) ? MusicHeartMuLaCatalog.CompanionRevision : MusicDiffRhythmCatalog.IsDiff(settings.Variation) ? MusicDiffRhythmCatalog.CompanionRevision : MusicAceCatalog.IsAce(settings.Variation) ? MusicAceCatalog.CompanionRevision : settings.Variation == MusicModelVariants.Bf16 ? MusicModelVariants.VaeRevision : MusicComponentCatalog.Revision,
                RuntimeRevision = MusicHeartMuLaCatalog.IsHeart(settings.Variation) ? MusicHeartMuLaCatalog.SourceRevision : MusicDiffRhythmCatalog.IsDiff(settings.Variation) ? MusicDiffRhythmCatalog.SourceRevision : MusicAceCatalog.IsAce(settings.Variation) ? MusicAceCatalog.SourceRevision : settings.Variation == MusicStudioRuntime.Variation ? MusicStudioRuntime.Revision : settings.Variation == MusicModelVariants.Bf16 ? MusicModelVariants.RuntimeRevision : MusicYueRuntime.Revision,
                RuntimePack = MusicHeartMuLaCatalog.IsHeart(settings.Variation) ? MusicHeartMuLaCatalog.RuntimeRevision : MusicDiffRhythmCatalog.IsDiff(settings.Variation) ? MusicDiffRhythmCatalog.RuntimeRevision : MusicAceCatalog.IsAce(settings.Variation) ? MusicAceCatalog.RuntimeRevision : settings.Variation == MusicStudioRuntime.Variation ? MusicStudioRuntime.Pack : settings.Variation == MusicModelVariants.Bf16 ? "pytorch-bf16" : Path.GetFileName(MusicYueRuntime.DirectoryPath), Expert = settings, Wishes = wishes?.Snapshot(), DurationAutomatic = durationAutomatic,
                Output = outputSettings, Artist = artist.Trim(), Comment = comment };
        if (settings.Variation == MusicModelVariants.Bf16 || MusicModelVariants.ExternalPipeline(settings.Variation)) job = job with { Variants = job.Variants.Select(v => v with { LanguageSeed = v.SoundSeed }).ToArray() };
        Save(job); return job;
        int Seed(string key, int index) => !settings.Values.ContainsKey(key) || settings.Integer(key) < 0 ? RandomNumberGenerator.GetInt32(1, int.MaxValue)
            : (int)(((long)settings.Integer(key) + index) % ((long)int.MaxValue + 1));
    }
    public string StagePath(string id, string suffix) => Path.Combine(Folder(id), Guid.NewGuid().ToString("N") + suffix);
    public IReadOnlyList<MusicTrack> Tracks(string id)
    {
        var job = Load(id);
        return job.Variants.Select((v, i) => (v, i)).Where(p => p.v.Completed && File.Exists(p.v.ResultPath))
            .Select(p => Track(job, p.v, p.i)).ToArray();
    }
    public static MusicTrack Track(MusicGenerationJob job, MusicGenerationVariant variant, int index) =>
        new(variant.ResultPath, Path.GetFileName(variant.ResultPath), variant.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds)
            : MusicWaveFile.ReadDuration(variant.ResultPath), job.CreatedAt)
        { JobId = job.Id, Variant = index, AdditionalPath = variant.AdditionalPath };
    private void Validate(MusicGenerationJob job)
    {
        if (job.Expert is null) throw new InvalidDataException("Missing music settings snapshot.");
        job.Expert.Validate();
        job.Output?.Validate();
        if (job.ProjectId is not null && (!Guid.TryParseExact(job.ProjectId, "N", out _) || job.ProjectStep is null or < 1)
            || job.ProjectId is null && job.ProjectStep is not null) throw new InvalidDataException("Invalid music project link.");
        var folder = Path.GetFullPath(Folder(job.Id)) + Path.DirectorySeparatorChar;
        var bf16 = job.Variation == MusicModelVariants.Bf16;
        var studio = job.Variation == MusicStudioRuntime.Variation;
        var ace = MusicAceCatalog.IsAce(job.Variation);
        var diff = MusicDiffRhythmCatalog.IsDiff(job.Variation);
        var heart = MusicHeartMuLaCatalog.IsHeart(job.Variation);
        if (job.Schema is not (1 or 2) || job.Schema == 1 && (bf16 || studio || ace || diff || heart) || !MusicModelVariants.Supported(job.Variation) || job.Expert.Variation != job.Variation
            || job.ModelRevision != MusicModelVariants.Revision(job.Variation)
            || job.DecoderRevision != (heart ? MusicHeartMuLaCatalog.CompanionRevision : diff ? MusicDiffRhythmCatalog.CompanionRevision : ace ? MusicAceCatalog.CompanionRevision : bf16 ? MusicModelVariants.VaeRevision : MusicComponentCatalog.Revision)
            || job.RuntimeRevision != (heart ? MusicHeartMuLaCatalog.SourceRevision : diff ? MusicDiffRhythmCatalog.SourceRevision : ace ? MusicAceCatalog.SourceRevision : studio ? MusicStudioRuntime.Revision : bf16 ? MusicModelVariants.RuntimeRevision : MusicYueRuntime.Revision)
            || (heart ? job.RuntimePack != MusicHeartMuLaCatalog.RuntimeRevision : diff ? job.RuntimePack != MusicDiffRhythmCatalog.RuntimeRevision : ace ? job.RuntimePack != MusicAceCatalog.RuntimeRevision : studio ? job.RuntimePack != MusicStudioRuntime.Pack : bf16 ? job.RuntimePack != "pytorch-bf16" : job.RuntimePack is not (MusicYueRuntime.CpuPack or MusicYueRuntime.CudaPack))
            || job.Variants.Length is < 1 or > 8 || !Path.IsPathFullyQualified(job.ModelsRoot)
            || !Path.IsPathFullyQualified(job.OutputFolder) || job.DurationSeconds is < 1 or > 360
            || diff && job.DurationSeconds > MusicDiffRhythmCatalog.MaximumDuration)
            throw new InvalidDataException("Unsupported music job.");
        foreach (var v in job.Variants)
        {
            if ((bf16 || ace || diff || heart) && v.LanguageSeed != v.SoundSeed) throw new InvalidDataException("This model requires one seed for all stages.");
            if (job.Output is null && (v.AdditionalPath is not null || v.AdditionalHash is not null)) throw new InvalidDataException("Unexpected duplicate in legacy music job.");
            if (v.LanguageSeed < 0 || v.SoundSeed < 0 || Path.GetDirectoryName(Path.GetFullPath(v.ResultPath)) != job.OutputFolder)
                throw new InvalidDataException("Invalid music variant.");
            if (job.Output is { } output && (Path.GetExtension(v.ResultPath) != MusicOutputSettings.Extension(output.Format) ||
                (output.AdditionalFormat is { } additional ? v.AdditionalPath != Path.ChangeExtension(v.ResultPath, MusicOutputSettings.Extension(additional)) : v.AdditionalPath is not null)))
                throw new InvalidDataException("Invalid additional audio path.");
            if (job.Output is not null && v.Completed && (v.ResultHash is null || v.DurationSeconds is null ||
                v.AdditionalPath is not null && v.AdditionalHash is null)) throw new InvalidDataException("Missing completed audio receipt.");
            if (v.DurationSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0) ||
                v.GenerationSeconds is { } elapsed && (!double.IsFinite(elapsed) || elapsed < 0)) throw new InvalidDataException("Invalid audio timing.");
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
    private static string UniquePath(string folder, string name, MusicOutputSettings? output)
    {
        for (var i = 0; i < 100000; i++)
        { var path = Path.Combine(folder, name + (i == 0 ? "" : "_" + i) + MusicOutputSettings.Extension(output?.Format ?? MusicAudioFormat.Wav));
            if (!File.Exists(path) && (output?.AdditionalFormat is not { } extra || !File.Exists(Path.ChangeExtension(path, MusicOutputSettings.Extension(extra))))) return path; }
        throw new IOException("No free music result name.");
    }
}
