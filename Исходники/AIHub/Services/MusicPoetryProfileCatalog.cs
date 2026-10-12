using System.IO;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicPoetryProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string RepositoryId { get; init; }
    public required string Revision { get; init; }
    public required string Architecture { get; init; }
    public required string Format { get; init; }
    public required string ReviewKey { get; init; }
    public string? SpecializationKey { get; init; }
    public required string License { get; init; }
    public long ParameterCount { get; init; }
    public double Temperature { get; init; } = .8;
    public double TopP { get; init; } = .95;
    public int TopK { get; init; } = 40;
    public double MinP { get; init; }
    public double RepeatPenalty { get; init; } = 1;
    public double PresencePenalty { get; init; }
    public required List<ManagedModelArtifactFile> Files { get; init; }
    public string ArtifactId => "model-poetry-" + Id;
    public ManagedModelArtifactFile Weight => Files.Single(file => file.Purpose == "main_model");
}

/// <summary>Pinned author artifacts; no network or model installation during discovery.</summary>
public static class MusicPoetryProfileCatalog
{
    public static IReadOnlyList<MusicPoetryProfile> All { get; } = Load();
    private static IReadOnlyList<MusicPoetryProfile> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIHub.Content.MusicPoetryProfiles.json")
            ?? throw new InvalidDataException("Missing songwriting artifact manifest.");
        var profiles = JsonSerializer.Deserialize<MusicPoetryProfile[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (profiles.Length != 6 || profiles.Any(p => p.Id is "lyrical" or "key2poetry") || profiles.Select(p => p.Id).Distinct().Count() != profiles.Length
            || profiles.Any(p => p.Revision.Length != 40 || !p.Revision.All(Uri.IsHexDigit)
                || p.Files.Count == 0 || p.Files.Any(f => f.SizeBytes <= 0 || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit))))
            throw new InvalidDataException("Incomplete songwriting artifact manifest.");
        return Array.AsReadOnly(profiles.OrderBy(p => p.Files.Sum(f => f.SizeBytes)).ToArray());
    }
    public static MusicPoetryProfile? Find(string candidateId) => All.FirstOrDefault(p => "profile-" + p.Id == candidateId);
    public static IReadOnlyList<ManagedModelArtifactCard> Cards(string root) => All.Select(p => Card(p, root)).ToArray();
    public static ManagedModelArtifactCard Card(MusicPoetryProfile profile, string root) => new()
    {
        ModelArtifactId = profile.ArtifactId, Family = profile.DisplayName, DisplayName = profile.DisplayName,
        Role = ManagedModelRoles.Tool, Provider = "Hugging Face", RepositoryId = profile.RepositoryId,
        Revision = profile.Revision, Format = profile.Format, Architecture = profile.Architecture,
        Quantization = profile.Format == "gguf" ? "Q4_K_M" : "FP32", ParameterCount = profile.ParameterCount,
        License = profile.License, SourcePage = "https://huggingface.co/" + profile.RepositoryId,
        IsManaged = true, CanRemoveFiles = true, IsPinned = false, ModelsRoot = root,
        InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Music", "Poetry", profile.Id),
        Origin = ManagedModelOrigins.PredefinedScenario,
        RuntimeBackend = profile.Format == "gguf" ? LlamaBackendPaths.DisplayName : "Managed Python / Transformers",
        Consumers = [new() { Id = "music-poetry", DisplayName = "Music songwriting chat", Kind = "subscenario" }],
        Files = profile.Files.Select(f => new ManagedModelArtifactFile { RelativePath = f.RelativePath,
            SourceUrl = f.SourceUrl, SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Purpose = f.Purpose }).ToList()
    };
    public static DebugModelInfo? Installed(MusicPoetryProfile profile, string root)
    {
        var card = Card(profile, root);
        if (string.IsNullOrEmpty(card.InstallDirectory)) return null;
        var path = Path.Combine(card.InstallDirectory, profile.Weight.RelativePath);
        if (!File.Exists(path) || new FileInfo(path).Length != profile.Weight.SizeBytes) return null;
        return new() { Name = profile.DisplayName, Path = path, Format = profile.Format,
            SizeBytes = profile.Files.Sum(f => f.SizeBytes), Role = ManagedModelRoles.Tool, IsRunnable = true };
    }
}
