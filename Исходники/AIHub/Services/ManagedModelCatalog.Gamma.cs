using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public const string OmniGammaArtifactId = "model-qwen3-8-27b-ridge-37bpw";
    public const string OmniGammaRepository = "empero-ai/Qwen3.8-27B-Ridge-GGUF";
    public const string OmniGammaRevision = "486faa5f2032ff99bdc8993ade1b8fff13d1464c";
    public const string OmniGammaSourceModel = "Qwen/Qwen3.8-27B";
    public const string OmniGammaDisplayName = "Qwen3.8-27B-Ridge 3.7 bpw + BF16 projector";

    public static ManagedModelArtifactCard CreateOmniGamma(string modelsRoot) => new()
    {
        ModelArtifactId = OmniGammaArtifactId,
        Family = "Qwen3.8-27B-Ridge", DisplayName = OmniGammaDisplayName,
        Role = ManagedModelRoles.Vision, Provider = "Hugging Face",
        RepositoryId = OmniGammaRepository, Revision = OmniGammaRevision,
        Format = "GGUF", Architecture = "qwen35", Quantization = "Ridge-3.7bpw",
        License = "Apache-2.0", SourcePage = $"https://huggingface.co/{OmniGammaRepository}",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Vision", "Qwen3.8-27B-Ridge-GGUF", OmniGammaRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = LlamaBackendPaths.DisplayName,
        Consumers = [Consumer("image-analysis-heavy", "Анализ изображений — Гамма", "scenario_bundle")],
        Files =
        [
            File("Qwen3.8-27B-Ridge-3.7bpw.gguf", $"https://huggingface.co/{OmniGammaRepository}/resolve/{OmniGammaRevision}/Qwen3.8-27B-Ridge-3.7bpw.gguf", 12_599_187_008,
                "95580dbdaad579582ee898257116abc18d7f3625a00c16a15735d41444a09f5e", "main_model"),
            File("mmproj-Qwen3.8-27B-BF16.gguf", $"https://huggingface.co/{OmniGammaRepository}/resolve/{OmniGammaRevision}/mmproj-Qwen3.8-27B-BF16.gguf", 931_145_952,
                "52228402ce4823f10705d901813cd43ced71859524cf2d8bf83305ad6b7dcbc2", "projector")
        ]
    };
}
