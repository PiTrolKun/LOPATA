using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicPoetryModelCandidate(string Id, string FileName, string Architecture,
    string PurposeKey, string ReviewKey, string LicenseId, bool InstalledOnly = false);
public sealed record MusicPoetryModelChoice(MusicPoetryModelCandidate Candidate, DebugModelInfo? Model);

/// <summary>Explicit songwriting candidates; arbitrary installed GGUFs are not chat models.</summary>
public static class MusicPoetryModelCatalog
{
    public static IReadOnlyList<MusicPoetryModelCandidate> All { get; } = Array.AsReadOnly(new[]
    {
        new MusicPoetryModelCandidate("core", CoreModelManager.CoreModelFileName, "qwen3", "Core", "Core", ManagedModelCatalog.CoreArtifactId),
        new MusicPoetryModelCandidate("beta", "Qwen3.8-9B-Q4_K_M.gguf", "qwen35", "Beta", "Beta", ManagedModelCatalog.OmniBetaArtifactId, true),
        new MusicPoetryModelCandidate("gamma", "Qwen3.8-27B-Ridge-3.7bpw.gguf", "qwen35", "Gamma", "Gamma", ManagedModelCatalog.OmniGammaArtifactId),
        new MusicPoetryModelCandidate("instruct", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "qwen3", "Instruct", "Instruct", "generation-qwen-instruct"),
        new MusicPoetryModelCandidate("executor", "qwen3.6-27b-q4_k_m.gguf", "qwen35", "Executor", "Executor", "sm54/Qwen3.6-27B-Q4_K_M-GGUF")
    });

    public static MusicPoetryModelCandidate? Find(DebugModelInfo model) => All.FirstOrDefault(candidate =>
        model.IsRunnable && model.Format.Equals("gguf", StringComparison.OrdinalIgnoreCase)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        { return false; }
    }

    public static IReadOnlyList<MusicPoetryModelChoice> Choices(StorageSettings settings)
    {
        var installed = FinancialModelDiscovery.Discover(settings).Where(IsCompatible).ToArray();
        return All.Select(candidate => new MusicPoetryModelChoice(candidate,
            installed.FirstOrDefault(model => Find(model)?.Id == candidate.Id)))
            .Where(choice => !choice.Candidate.InstalledOnly || choice.Model is not null).ToArray();
    }

    public static IReadOnlyList<DebugModelInfo> Discover(StorageSettings settings) => Choices(settings)
        .Where(choice => choice.Model is not null).Select(choice => choice.Model!).ToArray();
}
