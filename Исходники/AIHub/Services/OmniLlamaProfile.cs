using AIHub.Models;

namespace AIHub.Services;

public sealed record OmniLlamaProfile(string BundleId, string PipelineId, string PipelineVersion,
    string ArtifactId, string Repository, string Revision, string SourceModel, string Quantization, string Label)
{
    public static OmniLlamaProfile Alpha { get; } = new(ImageAnalysisBundleCatalog.LightId,
        ImageAnalysisPipelineIds.OmniAlpha, ImageAnalysisPipelineIds.OmniAlphaVersion,
        ManagedModelCatalog.OmniAlphaArtifactId, ManagedModelCatalog.OmniAlphaRepository,
        ManagedModelCatalog.OmniAlphaRevision, ManagedModelCatalog.OmniAlphaSourceModel, "Q5_K_M", "Alpha");
    public static OmniLlamaProfile Beta { get; } = new(ImageAnalysisBundleCatalog.MediumId,
        ImageAnalysisPipelineIds.OmniBeta, ImageAnalysisPipelineIds.OmniBetaVersion,
        ManagedModelCatalog.OmniBetaArtifactId, ManagedModelCatalog.OmniBetaRepository,
        ManagedModelCatalog.OmniBetaSessionRevision, ManagedModelCatalog.OmniBetaSourceModel, "Q4_K_M", "Beta");
    public static OmniLlamaProfile? ForBundle(string? bundle) => bundle switch
    {
        ImageAnalysisBundleCatalog.LightId => Alpha,
        ImageAnalysisBundleCatalog.MediumId => Beta,
        _ => null
    };
    public string DiagnosticProfile => "qwen35-" + Quantization.Replace("_", "").ToLowerInvariant() + "-f16";

    public void ApplyToNewSession(ImageAnalysisLiterarySession session)
    {
        session.BundleId = BundleId;
        session.PipelineId = PipelineId;
        session.PipelineVersion = PipelineVersion;
        session.ModelId = Repository;
        session.ModelRevision = Revision;
        session.RuntimeId = ImageAnalysisRuntimeIds.Qwen35Llama;
    }
}
