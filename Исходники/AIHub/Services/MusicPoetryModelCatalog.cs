using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicPoetryModelCandidate(string Id, string FileName, string Architecture,
    string PurposeKey, string ReviewKey, string LicenseId, bool InstalledOnly = false,
    string Format = "gguf", string? DisplayName = null, long DownloadBytes = 0, string? SpecializationKey = null);
public sealed record MusicPoetryModelChoice(MusicPoetryModelCandidate Candidate, DebugModelInfo? Model);

/// <summary>Explicit songwriting candidates; arbitrary installed GGUFs are not chat models.</summary>
public static class MusicPoetryModelCatalog
{
    public static IReadOnlyList<MusicPoetryModelCandidate> All { get; } = Array.AsReadOnly(new[]
    {
        new MusicPoetryModelCandidate("core", CoreModelManager.CoreModelFileName, "qwen3", "Core", "Core", ManagedModelCatalog.CoreArtifactId),
        new MusicPoetryModelCandidate("beta", "Qwen3.8-9B-Q4_K_M.gguf", "qwen35", "Beta", "Beta", ManagedModelCatalog.OmniBetaArtifactId, true),
        new MusicPoetryModelCandidate("gamma", "Qwen3.8-27B-Ridge-3.7bpw.gguf", "qwen35", "Gamma", "Gamma", ManagedModelCatalog.OmniGammaArtifactId, true),
        new MusicPoetryModelCandidate("instruct", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "qwen3", "Instruct", "Instruct", "generation-qwen-instruct", true),
        new MusicPoetryModelCandidate("executor", "qwen3.6-27b-q4_k_m.gguf", "qwen35", "Executor", "Executor", "sm54/Qwen3.6-27B-Q4_K_M-GGUF", true)
    }.Concat(MusicPoetryProfileCatalog.All.Select(p => new MusicPoetryModelCandidate("profile-" + p.Id,
        p.Weight.RelativePath, p.Architecture, p.ReviewKey, p.ReviewKey, p.ArtifactId,
        Format: p.Format, DisplayName: p.DisplayName, DownloadBytes: p.Files.Sum(f => f.SizeBytes),
        SpecializationKey: p.SpecializationKey))).ToArray());

    public static MusicPoetryModelCandidate? Find(DebugModelInfo model) => All.FirstOrDefault(candidate =>
        model.IsRunnable && model.Format.Equals(candidate.Format, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(model.Path).Equals(candidate.FileName, StringComparison.OrdinalIgnoreCase));

    public static bool IsCompatible(DebugModelInfo model)
    {
        var candidate = Find(model);
        if (candidate is null) return false;
        try
        {
            var metadata = LiteraryModelMemoryMetadata.Read(model.Path);
            return metadata.Architecture.Equals(candidate.Architecture, StringComparison.Ordinal)
                && metadata.ModelContextTokens > 512;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        { return false; }
    }

    public static IReadOnlyList<MusicPoetryModelChoice> Choices(StorageSettings settings)
    {
        var roots = settings.Models.Locations.Select(l => l.Path?.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        var installed = FinancialModelDiscovery.Discover(settings).Concat(roots.SelectMany(root =>
            MusicPoetryProfileCatalog.All.Select(profile => MusicPoetryProfileCatalog.Installed(profile, root!))
                .Where(model => model is not null).Select(model => model!))).Where(IsCompatible).ToArray();
        return All.Select(candidate => new MusicPoetryModelChoice(candidate,
            installed.FirstOrDefault(model => Find(model)?.Id == candidate.Id)))
            .Where(choice => !choice.Candidate.InstalledOnly || choice.Model is not null)
            .OrderBy(choice => choice.Candidate.Id == "core" ? 0 : 1)
            .ThenBy(choice => choice.Candidate.DownloadBytes > 0 ? choice.Candidate.DownloadBytes : choice.Model?.SizeBytes ?? 0).ToArray();
    }

    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings) => Choices(settings)
        .Where(choice => choice.Model is not null).Select(choice => choice.Model!).ToArray();
}
