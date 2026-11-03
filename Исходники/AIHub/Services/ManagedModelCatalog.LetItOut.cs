using AIHub.Models;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public const string LetItOutArtifactId = "model-gemma4-26b-a4b-it-qat-udq4kxl";
    public const string LetItOutRepository = "unsloth/gemma-4-26B-A4B-it-qat-GGUF";
    public const string LetItOutRevision = "7b92b5b28818151e8669af2e45e88d6086f490dd";
    public const string LetItOutVisionArtifactId = "model-gemma4-26b-a4b-vision-f16";
    public static ManagedModelArtifactCard CreateLetItOutVision(string root) => new()
    {
        ModelArtifactId = LetItOutVisionArtifactId, Family = "Gemma 4 26B A4B Instruct QAT",
        DisplayName = "Gemma 4 · image module / зрительный модуль", Role = ManagedModelRoles.Executor,
        Provider = "Hugging Face", RepositoryId = LetItOutRepository, Revision = LetItOutRevision,
        Format = "GGUF", Architecture = "gemma4", Quantization = "F16", License = "Apache-2.0",
        SourcePage = $"https://huggingface.co/{LetItOutRepository}", IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
        InstallDirectory = CombineIfRoot(root, "Experimental", "Gemma4", "Vision"),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "llama.cpp mtmd (CPU)",
        Consumers = [Consumer("let_it_out", "Если накипело", "scenario_bundle")],
        Files = [File("mmproj-F16.gguf", $"https://huggingface.co/{LetItOutRepository}/resolve/{LetItOutRevision}/mmproj-F16.gguf",
            1_193_058_784, "d00f211a7d4f7fb19bd9b75d8e9342eccffb5920b08fd9562167560fdfcc5dd1", "vision_projector")]
    };

    public static ManagedModelArtifactCard CreateLetItOut(string modelsRoot) => new()
    {
        ModelArtifactId = LetItOutArtifactId,
        Family = "Gemma 4 26B A4B Instruct QAT",
        DisplayName = LetItOutModel.DisplayName,
        Role = ManagedModelRoles.Executor,
        Provider = "Hugging Face", RepositoryId = LetItOutRepository, Revision = LetItOutRevision,
        Format = "GGUF", Architecture = "gemma4", Quantization = "UD-Q4_K_XL",
        ParameterCount = 26_000_000_000, License = "Apache-2.0",
        SourcePage = $"https://huggingface.co/{LetItOutRepository}",
        IsManaged = true, CanRemoveFiles = true, ModelsRoot = modelsRoot,
        // Reuse the evaluated download, without copying fourteen gigabytes or changing the global core.
        InstallDirectory = CombineIfRoot(modelsRoot, "Experimental", "Gemma4", "unsloth--gemma-4-26B-A4B-it-qat-GGUF"),
        Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "llama.cpp (Vulkan / CPU)",
        Consumers = [Consumer("let_it_out", "Если накипело", "scenario_bundle")],
        Files = [File(LetItOutModel.FileName,
            $"https://huggingface.co/{LetItOutRepository}/resolve/{LetItOutRevision}/{LetItOutModel.FileName}",
            LetItOutModel.TotalBytes, "a7c5bc715f5ff8e99a3e8901ce7d2b42b402c669bf24f7c5250747633d0f5891", "main_model")]
    };
}
