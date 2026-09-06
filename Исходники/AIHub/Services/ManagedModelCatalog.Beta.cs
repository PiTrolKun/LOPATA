using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public const string OmniBetaArtifactId = "model-qwen3-8-9b-distill-q4km";
    public const string OmniBetaRepository = "empero-ai/Qwen3.8-9B-Distill-GGUF";
    public const string OmniBetaRevision = "760121cd70bb4c36b2b5ec58eb765e0df5987efe";
    public const string OmniBetaProjectorRepository = "unsloth/Qwen3.5-9B-GGUF";
    public const string OmniBetaProjectorRevision = "3885219b6810b007914f3a7950a8d1b469d598a5";
    public const string OmniBetaSessionRevision = OmniBetaRevision + ":" + OmniBetaProjectorRevision;
    public const string OmniBetaDisplayName = "Qwen3.8-9B-Distill Q4_K_M + Qwen3.5-9B F16 projector";
    public const string OmniBetaSourceModel = "empero-ai/Qwen3.8-9B-Distill";

    public static ManagedModelArtifactCard CreateOmniBeta(string modelsRoot) => new()
    {
        ModelArtifactId = OmniBetaArtifactId,
        Family = "Qwen3.8-9B-Distill", DisplayName = OmniBetaDisplayName,
        Role = ManagedModelRoles.Vision, Provider = "Hugging Face",
        RepositoryId = OmniBetaRepository, Revision = OmniBetaSessionRevision,
        Format = "GGUF", Architecture = "qwen35", Quantization = "Q4_K_M",
        License = "Apache-2.0",
        SourcePage = $"https://huggingface.co/{OmniBetaRepository}",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = modelsRoot,
        InstallDirectory = CombineIfRoot(modelsRoot, "Vision", "Qwen3.8-9B-Distill-GGUF", OmniBetaRevision[..12] + "-" + OmniBetaProjectorRevision[..12]),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = LlamaBackendPaths.DisplayName,
        Consumers = [Consumer("image-analysis-medium", "Анализ изображений — Бета", "scenario_bundle")],
        Files =
        [
            File("Qwen3.8-9B-Q4_K_M.gguf", $"https://huggingface.co/{OmniBetaRepository}/resolve/{OmniBetaRevision}/Qwen3.8-9B-Q4_K_M.gguf", 5_780_090_176,
                "df13d66021cef676f82be74053220fd75af6bf2a6a7fb77f5222ab9e50744a7a", "main_model"),
            File("mmproj-Qwen3.5-9B-F16.gguf", $"https://huggingface.co/{OmniBetaProjectorRepository}/resolve/{OmniBetaProjectorRevision}/mmproj-F16.gguf", 918_166_080,
                "f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f", "projector")
        ]
    };
}
