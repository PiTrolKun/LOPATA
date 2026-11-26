using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    internal const string Qwen36ExecutorArtifactId = "model-fcb9b1508fc0d3f38bc2f3e5";
    internal const string Qwen36ExecutorRepository = "sm54/Qwen3.6-27B-Q4_K_M-GGUF";
    internal const string Qwen36ExecutorRevision = "b3af3a64a15eb981da65f9074f81f1cc0804be75";
    internal const string Qwen36ExecutorFile = "qwen3.6-27b-q4_k_m.gguf";
    internal const string Qwen36ExecutorSha256 = "2ac61afbc9c600899ab57fe6333116179b9b08a6230c9bcaca5afd62ad75b7d7";

    internal static void ReuseQwen36ExecutorDirectory(ManagedModelArtifactCard card, ManagedModelLibraryStore library)
    {
        var registered = library.Load(card.ModelArtifactId);
        var weight = card.Files.Single();
        if (registered is { IsManaged: true } && Path.IsPathFullyQualified(registered.InstallDirectory) &&
            ModelStorageAddresses.IsBelow(card.ModelsRoot, registered.InstallDirectory) &&
            registered.Files.Any(file => file.IsRequired && file.RelativePath == weight.RelativePath &&
                file.SizeBytes == weight.SizeBytes && file.Sha256.Equals(weight.Sha256, StringComparison.OrdinalIgnoreCase)))
            card.InstallDirectory = registered.InstallDirectory;
    }

    internal static ManagedModelArtifactCard CreateQwen36Executor(string root) => new()
    {
        ModelArtifactId = Qwen36ExecutorArtifactId, Family = "Qwen3.6-27B", DisplayName = Qwen36ExecutorRepository,
        Role = ManagedModelRoles.Executor, Provider = "Hugging Face", RepositoryId = Qwen36ExecutorRepository,
        Revision = Qwen36ExecutorRevision, Format = "GGUF", Architecture = "qwen35", Quantization = "Q4_K_M",
        ParameterCount = 27_000_000_000, License = "Apache-2.0", SourcePage = $"https://huggingface.co/{Qwen36ExecutorRepository}",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Executors", Qwen36ExecutorRepository.Replace('/', '_')),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = LlamaBackendPaths.DisplayName,
        Files = [new()
        {
            RelativePath = Qwen36ExecutorFile, SizeBytes = 16_547_398_976, Sha256 = Qwen36ExecutorSha256,
            SourceUrl = $"https://huggingface.co/{Qwen36ExecutorRepository}/resolve/{Qwen36ExecutorRevision}/{Qwen36ExecutorFile}",
            Purpose = "executor_model", IsRequired = true
        }]
    };
}
