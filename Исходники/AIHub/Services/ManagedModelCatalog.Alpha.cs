using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public const string OmniAlphaArtifactId = "model-qwen3-8-4b-distill-q5km";
    public const string OmniAlphaRepository = "mradermacher/Qwen3.8-4B-Distill-GGUF";
    public const string OmniAlphaRevision = "796f0c8fbdab2e6e0c14a13d499075850e4a16b3";
    public const string OmniAlphaDisplayName = "Qwen3.8-4B-Distill Q5_K_M + F16 projector";
    public const string OmniAlphaSourceModel = "empero-ai/Qwen3.8-4B-Distill";

    public static ManagedModelArtifactCard CreateOmniAlpha(string modelsRoot) => new()
    {
        ModelArtifactId = OmniAlphaArtifactId,
        Family = "Qwen3.8-4B-Distill", DisplayName = OmniAlphaDisplayName,
        Role = ManagedModelRoles.Vision, Provider = "Hugging Face",
        RepositoryId = OmniAlphaRepository, Revision = OmniAlphaRevision,
        Format = "GGUF", Architecture = "qwen35", Quantization = "Q5_K_M",
        ParameterCount = 4_659_865_088,
        License = "Apache-2.0",
        SourcePage = $"https://huggingface.co/{OmniAlphaRepository}",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Vision", "Qwen3.8-4B-Distill-GGUF", OmniAlphaRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = LlamaBackendPaths.DisplayName,
        Consumers = [Consumer("image-analysis-light", "Анализ изображений — Альфа", "scenario_bundle")],
        Files =
        [
            AlphaFile("Qwen3.8-4B-Distill.Q5_K_M.gguf", 3_161_426_176,
                "405bdf80449c44189cfc94a586e44c22cc84221954f2551c372c16084a2def24", "main_model"),
            AlphaFile("Qwen3.8-4B-Distill.mmproj-f16.gguf", 672_423_552,
                "7afeb5dbd82a64cf4295edbd73fa889c9020961d160eba76772a18aa069d9f74", "projector")
        ]
    };

    private static ManagedModelArtifactFile AlphaFile(string name, long size, string hash, string purpose) =>
        File(name, $"https://huggingface.co/{OmniAlphaRepository}/resolve/{OmniAlphaRevision}/{name}", size, hash, purpose);
}
