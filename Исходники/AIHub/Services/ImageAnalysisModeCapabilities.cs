using AIHub.Models;

namespace AIHub.Services;

public static class ImageAnalysisModeCapabilities
{
    public static bool UsesOmniConversation(string? bundleId) =>
        bundleId is ImageAnalysisBundleCatalog.LightId or ImageAnalysisBundleCatalog.HeavyId;

    public static string Pipeline(string? bundleId) => bundleId switch
    {
        ImageAnalysisBundleCatalog.LightId => ImageAnalysisPipelineIds.OmniAlpha,
        ImageAnalysisBundleCatalog.HeavyId => ImageAnalysisPipelineIds.OmniHeavy,
        _ => ImageAnalysisPipelineIds.Legacy
    };

    public static string VisionArtifact(string? bundleId) => bundleId switch
    {
        ImageAnalysisBundleCatalog.LightId => ManagedModelCatalog.OmniAlphaArtifactId,
        ImageAnalysisBundleCatalog.HeavyId => ManagedModelCatalog.Qwen25OmniHeavyArtifactId,
        _ => ManagedModelCatalog.KimiMediumArtifactId
    };
}
