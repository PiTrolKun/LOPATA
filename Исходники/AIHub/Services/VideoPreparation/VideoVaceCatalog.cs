using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Public, immutable acquisition inputs for the native ComfyUI VACE bundle.</summary>
internal static class VideoVaceCatalog
{
    internal const string ModelId = "wan-vace-1.3b";
    internal const string ComponentId = "video-vace13-models";
    internal const string Repository = "Comfy-Org/Wan_2.1_ComfyUI_repackaged";
    internal const string Revision = "123acf1cc74bccbb9bfff8ac1ee72edc08c2341d";
    internal static IReadOnlyList<string> ComponentIds { get; } = [ComponentId];
    internal sealed record FilePin(string Path, long Bytes, string Sha256);
    // Exact revision metadata was checked anonymously; no account or access token is required.
    internal static IReadOnlyList<FilePin> Files { get; } = [
        new("split_files/diffusion_models/wan2.1_vace_1.3B_fp16.safetensors", 4_309_519_800,
            "640ccc0577e6a5d4bb15cd91b11b699ef914fc55f126c5a1c544e152130784f2"),
        new("split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", 6_735_906_897,
            "c3355d30191f1f066b26d93fba017ae9809dce6c627dda5f6a66eaa651204f68"),
        new("split_files/vae/wan_2.1_vae.safetensors", 253_815_318,
            "2fc39d31359a4b0a64f55876d8ff7fa8d780956ae2cb13463b0223e15148976b")];

    internal static IReadOnlyList<ManagedModelArtifactCard> Cards(string modelsRoot)
    {
        if (string.IsNullOrWhiteSpace(modelsRoot) || !Path.IsPathFullyQualified(modelsRoot))
            throw new InvalidOperationException("VideoEditor.StorageRequired");
        var root = Path.GetFullPath(modelsRoot);
        return [new()
        {
            ModelArtifactId = ComponentId, Family = "Wan2.1 VACE", DisplayName = "Wan2.1 VACE 1.3B · FP16 + UMT5 FP8 + VAE",
            Role = ManagedModelRoles.Tool, Provider = "ComfyUI", RepositoryId = Repository, Revision = Revision,
            Format = "pinned-inputs", License = "Apache-2.0", SourcePage = "https://huggingface.co/" + Repository + "/tree/" + Revision,
            IsManaged = true, IsPinned = true, IsSystem = false, CanRemoveFiles = false,
            Origin = ManagedModelOrigins.PredefinedScenario, ModelsRoot = root,
            InstallDirectory = Path.Combine(root, "Video", "VACE-1.3B", ComponentId, Revision),
            Files = Files.Select(f => new ManagedModelArtifactFile
            {
                RelativePath = f.Path, SizeBytes = f.Bytes, Sha256 = f.Sha256, IsRequired = true, Purpose = "vace-acquisition",
                SourceUrl = "https://huggingface.co/" + Repository + "/resolve/" + Revision + "/" + f.Path
            }).ToList(),
            Consumers = [new() { Id = "video_generation", DisplayName = "Wan2.1 VACE 1.3B", Kind = "scenario" }]
        }];
    }
}
