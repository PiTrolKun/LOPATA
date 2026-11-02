using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.SongClip;

internal sealed class SongClipProject
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Song { get; set; } = "";
    public string Reference { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipReference[]? References { get; set; }
    public string Lyrics { get; set; } = "";
    public string Timings { get; set; } = "";
    public string Model { get; set; } = "glitchframe";
    public string PendingAttemptId { get; set; } = "";
    public SongClipSettings Settings { get; set; } = new();
    public List<SongClipResult> Results { get; set; } = [];
}
internal sealed record SongClipSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 24;
    public int Steps { get; set; } = 20;
    public int Interval { get; set; } = 8;
    public long Seed { get; set; } = -1;
    public string Prompt { get; set; } = "Cinematic music video artwork, luminous abstract landscapes, teal and amber light, rich depth, no text";
    public string Shader { get; set; } = "spectral_milkdrop";
    public double Intensity { get; set; } = .7;
    public int Quality { get; set; } = 85;
    public SongClipMotionSettings Motion { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipMontageSettings? Montage { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipGeekSettings? Geek { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipInfiniteTalkSettings? InfiniteTalk { get; set; }
}
internal sealed record SongClipInfiniteTalkSettings
{
    public int Width { get; set; } = 448;
    public int Height { get; set; } = 448;
    public int Steps { get; set; } = 40;
    public int FrameWindow { get; set; } = 81;
    public int MotionFrames { get; set; } = 9;
    public double AudioCfg { get; set; } = 4;
    public double VramReserveGb { get; set; } = 2;
}
internal sealed record SongClipGeekSettings
{
    public int Width { get; set; } = 608;
    public int Height { get; set; } = 352;
    public int Steps { get; set; } = 20;
    public int CardSteps { get; set; } = 20;
    public int ClipSeconds { get; set; } = 6;
    public double ReferenceStrength { get; set; } = .55;
}
internal sealed record SongClipMotionSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }
    public int Width { get; set; } = 608;
    public int Height { get; set; } = 352;
    public int RawFrames { get; set; } = 243;
    public int ContextFrames { get; set; } = 39;
    public int Steps { get; set; } = 20;
}
internal sealed record SongClipMontageSettings
{
    public int Width { get; set; } = 608;
    public int Height { get; set; } = 352;
    public int Steps { get; set; } = 20;
    public int ClipSeconds { get; set; } = 5;
}
internal sealed record SongClipResult(string Id, string File, string Model, double Seconds, DateTimeOffset CreatedUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }
}
internal sealed record SongClipAttempt(string Id, string ProjectId, string Song, string Reference, string Name,
    string Lyrics, string Timings, SongClipSettings Settings, string ModelsRoot, int Connections)
{
    internal const string Kind = "song.clip.glitchframe";
    public string Model { get; init; } = "glitchframe";
    // Missing in historical snapshots: they must resume with their original inputs.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PlanningVersion { get; init; }
    // Null denotes the exact historical single-reference contract.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SongClipReference[]? References { get; init; }
}

/// <summary>Small atomic workspace documents, immutable per-attempt inputs and owned binary attachments.</summary>
internal sealed class SongClipStore(string? directory = null)
{
    internal string Root { get; } = Path.GetFullPath(directory ?? Path.Combine(AppDataPaths.BaseDirectory, "SongClip", "Projects"));
    internal string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid song-clip identity.");
        return Path.Combine(Root, id);
    }
    internal void Save(SongClipProject project)
    {
        SongClipReferences.ValidateDraft(project.Reference, project.References);
        foreach (var reference in SongClipReferences.For(project)) _ = Resolve(project.Id, reference.File);
        project.UpdatedUtc = DateTimeOffset.UtcNow;
        var file = Path.Combine(Folder(project.Id), "project.json");
        Atomic(file, project);
    }
    internal bool SaveDraft(SongClipProject project)
    {
        var folder = Folder(project.Id);
        if (!File.Exists(Path.Combine(folder, "project.json")) && !HasDraftContent(project) && !HasStoredMaterial(folder))
            return false;
        Save(project);
        return true;
    }
    internal SongClipProject Load(string id)
    {
        var file = Path.Combine(Folder(id), "project.json");
        var project = JsonSerializer.Deserialize<SongClipProject>(File.ReadAllText(file)) ?? throw new InvalidDataException("Empty project.");
        if (project.Schema != 1 || project.Id != id) throw new InvalidDataException("Unsupported song-clip project.");
        if(project.PendingAttemptId.Length>0&&!Guid.TryParseExact(project.PendingAttemptId,"N",out _))throw new InvalidDataException("Invalid pending attempt.");
        foreach (var attachment in new[] { project.Song, project.Reference }.Where(x => x.Length > 0)) _ = Resolve(id, attachment);
        SongClipReferences.ValidateDraft(project.Reference, project.References);
        foreach (var reference in SongClipReferences.For(project)) _ = Resolve(id, reference.File);
        return project;
    }
    internal IReadOnlyList<SongClipProject> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root).Where(p => Guid.TryParseExact(Path.GetFileName(p), "N", out _))
            .Where(p => File.Exists(Path.Combine(p, "project.json"))).Select(p => Load(Path.GetFileName(p)))
            .Where(p => HasDraftContent(p) || HasStoredMaterial(Folder(p.Id)))
            .OrderByDescending(p => p.Pinned).ThenByDescending(p => p.UpdatedUtc).ToArray();
    }
    private static bool HasDraftContent(SongClipProject project) => project.Pinned || project.Results.Count > 0
        || project.References is { Length: > 0 }
        || new[] { project.Name, project.Song, project.Reference, project.Lyrics, project.Timings, project.PendingAttemptId }
            .Any(value => !string.IsNullOrWhiteSpace(value))
        || project.Model != "glitchframe" || (string.IsNullOrWhiteSpace(project.Settings.Prompt)
            ? project.Settings with { Prompt = new SongClipSettings().Prompt } : project.Settings) != new SongClipSettings();
    // Legacy empty documents may be hidden, but any owned attachment or attempt
    // keeps the project visible. Listing never removes history or user material.
    private static bool HasStoredMaterial(string folder) => Directory.Exists(folder)
        && Directory.EnumerateFileSystemEntries(folder).Any(path => Path.GetFileName(path) != "project.json");
    internal string Resolve(string id, string relative) => RuntimeFileAudit.Contained(Folder(id), relative);
    internal void EnsureAttemptSnapshot(SongClipAttempt attempt)
    {
        SongClipReferences.ValidateNewAttempt(attempt);
        foreach (var reference in SongClipReferences.For(attempt)) _ = Resolve(attempt.ProjectId, reference.File);
        var path = Resolve(attempt.ProjectId, "attempts/" + Guid.ParseExact(attempt.Id, "N").ToString("N") + "/snapshot.json");
        if (!File.Exists(path)) { Atomic(path, attempt); return; }
        // Typed defaults retain compatibility with earlier Glitchframe snapshots.
        // Never rewrite the original input, even when optional fields were absent.
        var saved = JsonSerializer.Deserialize<SongClipAttempt>(File.ReadAllText(path));
        if (saved is null || saved with { References = attempt.References } != attempt
            || !SongClipReferences.Same(saved.References, attempt.References))
            throw new InvalidDataException("SongClip.SnapshotMismatch");
    }
    internal async Task<string> ImportAsync(string id, string source, CancellationToken token)
    {
        using var input = File.OpenRead(source);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)); input.Position = 0;
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension.Length > 12 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException("Invalid attachment name.");
        var relative = "attachments/" + hash + extension;
        var target = Resolve(id, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) return relative;
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await using (var output = File.Create(staging)) { await input.CopyToAsync(output, token); await output.FlushAsync(token); } token.ThrowIfCancellationRequested(); File.Move(staging, target, false); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return relative;
    }
    internal static void Atomic<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(output, value, new JsonSerializerOptions { WriteIndented = true }); output.Flush(true); } File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
