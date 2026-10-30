using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Pinned acquisition coordinates. Empty hashes are unresolved, never an integrity receipt.</summary>
internal static class VideoLtxCatalog
{
    internal const string ModelId = "ltx-2.5";
    internal const string ComponentId = "video-ltx25-models";
    internal const string Repository = "Lightricks/LTX-2.5";
    internal const string Revision = "49b093a39c19169a8a81721dcc541e07e945232f";
    internal static IReadOnlyList<string> ComponentIds { get; } = [ComponentId];
    internal sealed record FilePin(string Path, long Bytes);
    internal static IReadOnlyList<FilePin> Files { get; } = [
        new("diffusion_models/ltx-2.5-22b-distilled-transformer-comfy-int8-convrot.safetensors", 21_504_034_224),
        new("text_encoders/gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors", 15_372_969_374),
        new("vae/ltx-2.5-video-vae-conv-bf16.safetensors", 1_452_269_922),
        new("vae/ltx-2.5-audio-vae-bf16.safetensors", 364_866_540),
        new("latent_upscale_models/ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors", 995_778_752)];

    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Path.IsPathFullyQualified(modelsRoot))
            throw new InvalidOperationException("VideoEditor.StorageRequired");
        var root = Path.GetFullPath(modelsRoot);
        return [new()
        {
            ModelArtifactId = ComponentId, Family = "LTX-2.5", DisplayName = "LTX-2.5 Distilled · INT8 ConvRot + Conv VAE",
            Role = ManagedModelRoles.Tool, Provider = "ComfyUI", RepositoryId = Repository, Revision = Revision,
            Format = "pinned-inputs", License = "LTX-2.x Community License Agreement (2026-08-11)",
            SourcePage = "https://huggingface.co/" + Repository + "/tree/" + Revision,
            IsManaged = true, IsPinned = true, CanRemoveFiles = false, Origin = ManagedModelOrigins.PredefinedScenario,
            ModelsRoot = root, InstallDirectory = Path.Combine(root, "Video", "LTX-2.5", ComponentId, Revision),
            Files = Files.Select(f => new ManagedModelArtifactFile
            {
                RelativePath = f.Path, SizeBytes = f.Bytes, Sha256 = "", IsRequired = true, Purpose = "ltx25-acquisition",
                SourceUrl = "https://huggingface.co/" + Repository + "/resolve/" + Revision + "/" + f.Path
            }).ToList(),
            Consumers = [new() { Id = "video_generation", DisplayName = "LTX-2.5", Kind = "scenario" }]
        }];
    }
}
