using System.IO;
namespace AIHub.Services.VideoPreparation;

/// <summary>Explicit binding of one H3 profile to its pinned weights; never falls back to a different encoder.</summary>
internal static class VideoH3Profile
{
    internal static Dictionary<string, string> Models(bool lite) => new()
    {
        ["Diffusion"] = lite ? "minimax_h3_ref2va_pruned_w4a8_mixed.safetensors" : "minimax_h3_ref2va_pruned_int8_convrot.safetensors",
        ["Encoder"] = lite ? "qwen3vl_4b_int4_convrot.safetensors" : "qwen3vl_32b_minimax_h3_int8_convrot.safetensors",
        ["VideoVae"] = "minimax_h3_video_vae_int8_convrot.safetensors", ["AudioVae"] = "minimax_h3_audio_vae_fp32.safetensors",
        ["Projection"] = lite ? "mmh3-4b-ClipProj-v3.1-mlp.safetensors" : ""
    };
    internal static Dictionary<string, string> Folders(VideoH3Environment environment, bool lite)
    {
        string Root(string id) => environment.Cards.Single(c => c.ModelArtifactId == id).InstallDirectory;
        if (lite) return new() { ["diffusion_models"] = Root("video-h3lite-models"), ["text_encoders"] = Root("video-h3lite-encoder"),
            ["vae"] = Path.Combine(Root("video-h3lite-vaes"), "vae"), ["clip_projections"] = Root("video-h3lite-projection") };
        var root = Root("video-h3-models");
        return new() { ["diffusion_models"] = Path.Combine(root, "diffusion_models"), ["text_encoders"] = Path.Combine(root, "text_encoders"),
            ["vae"] = Path.Combine(root, "vae"), ["vae_approx"] = Path.Combine(Root("video-h3-preview-model"), "vae_approx"),
            ["latent_upscale_models"] = Path.Combine(Root("video-h3-upscale-model"), "latent_upscale_models") };
    }
    internal static OutputModelUse[] Facts(VideoH3Environment environment) => environment.Cards
        .Where(c => c.ModelArtifactId.StartsWith("video-h3lite-", StringComparison.Ordinal) || c.ModelArtifactId is "video-h3-source-comfy" or "video-h3-source-timeline")
        .SelectMany(c => c.Files.Select(f => new OutputModelUse(c.ModelArtifactId + "/" + f.RelativePath, f.RelativePath, c.Revision,
            c.ModelArtifactId switch { "video-h3lite-models" => "video and audio diffusion", "video-h3lite-encoder" => "text and image encoder",
                "video-h3lite-projection" => "encoder projection", "video-h3lite-vaes" => "decoder", _ => "runtime" }))).ToArray();
}
