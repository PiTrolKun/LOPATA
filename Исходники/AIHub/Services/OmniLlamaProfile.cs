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
    public static OmniLlamaProfile Gamma { get; } = new(ImageAnalysisBundleCatalog.HeavyId,
        ImageAnalysisPipelineIds.OmniGamma, ImageAnalysisPipelineIds.OmniGammaVersion,
        ManagedModelCatalog.OmniGammaArtifactId, ManagedModelCatalog.OmniGammaRepository,
        ManagedModelCatalog.OmniGammaRevision, ManagedModelCatalog.OmniGammaSourceModel, "Ridge-3.7bpw", "Gamma")
        { ProjectorQuantization = "BF16", Temperature = 1.0 };
    public string ProjectorQuantization { get; init; } = "F16";
    public double Temperature { get; init; } = 0.6;

    public static OmniLlamaProfile? ForBundle(string? bundle) => bundle switch
    {
        ImageAnalysisBundleCatalog.LightId => Alpha,
        ImageAnalysisBundleCatalog.MediumId => Beta,
        ImageAnalysisBundleCatalog.HeavyId => Gamma,
        _ => null
    };
    public string DiagnosticProfile => "qwen35-" + Quantization.Replace("_", "").ToLowerInvariant() + "-" + ProjectorQuantization.ToLowerInvariant();

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
