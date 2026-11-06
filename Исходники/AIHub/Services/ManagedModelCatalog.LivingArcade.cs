using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Services;

public static partial class ManagedModelCatalog
{
    public static ManagedModelArtifactCard CreateLivingArcadeMain(string root) => ArcadeCard(root, false);
    public static ManagedModelArtifactCard CreateLivingArcadeArtist(string root) => ArcadeCard(root, true);

    private static ManagedModelArtifactCard ArcadeCard(string root, bool artist)
    {
        var size = artist ? "3B" : "7B";
        var repository = $"Qwen/Qwen2.5-Coder-{size}-Instruct-GGUF";
        var revision = artist ? LivingArcadeModels.ArtistRevision : LivingArcadeModels.MainRevision;
        var file = artist ? LivingArcadeModels.ArtistFile : LivingArcadeModels.MainFile;
        return new()
        {
            ModelArtifactId = artist ? LivingArcadeModels.ArtistArtifactId : LivingArcadeModels.MainArtifactId,
            Family = $"Qwen2.5-Coder-{size}-Instruct", DisplayName = $"Qwen2.5-Coder {size} Instruct · Q4_K_M",
            Role = ManagedModelRoles.Executor, Provider = "Hugging Face", RepositoryId = repository,
            Revision = revision, Format = "GGUF", Architecture = "qwen2", Quantization = "Q4_K_M",
            ParameterCount = artist ? 3_090_000_000 : 7_610_000_000,
            License = artist ? "Qwen Research" : "Apache-2.0",
            SourcePage = $"https://huggingface.co/{repository}", IsManaged = true, CanRemoveFiles = true,
            ModelsRoot = root, InstallDirectory = CombineIfRoot(root, "Experimental", "LivingArcade", $"Qwen2.5-Coder-{size}-Instruct-GGUF"),
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "llama.cpp (Vulkan / CPU)",
            Consumers = [Consumer("living_arcade", "Живая аркада", "scenario_bundle")],
            Files = [File(file, $"https://huggingface.co/{repository}/resolve/{revision}/{file}",
                artist ? LivingArcadeModels.ArtistBytes : LivingArcadeModels.MainBytes,
                artist ? "724fb256bec1ff062b2f65e4569e871ad2e95ab2a3989723d1769c54294730b7"
                    : "509287f78cb4d4cf6b3843734733b914b2c158e43e22a7f4bf5e963800894d3c", "main_model")]
        };
    }
}
