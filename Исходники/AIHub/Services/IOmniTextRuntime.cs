using AIHub.Models;

namespace AIHub.Services;

// One conversation pipeline, with transport and model identity supplied by the runtime.
public interface IOmniTextRuntime : IDisposable
{
    string BundleId => ImageAnalysisBundleCatalog.HeavyId;
    string PipelineId => ImageAnalysisPipelineIds.OmniHeavy;
    string PipelineVersion => ImageAnalysisPipelineIds.OmniHeavyVersion;
    string ModelId => ManagedModelCatalog.Qwen25OmniRepository;
    string ModelRevision => ManagedModelCatalog.Qwen25OmniRevision;
    string RuntimeId => ImageAnalysisRuntimeIds.Qwen25OmniTransformers;
    string RuntimeVersion { get; }
    string DeviceMapJson { get; }
    bool IsReady { get; }
    ImageAnalysisHeavyResourcePlan? CurrentPlan { get; }
    Task<OmniWarmupResult> PrepareAsync(Action<string> log,
        IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken cancellationToken,
        bool reuseCurrentPlan = false);
    Task<OmniTextGenerationResult> GenerateAsync(string command, string imagePath,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, IProgress<ModelStreamChunk>? streamProgress,
        CancellationToken cancellationToken, Action<string>? responseReceived = null,
        Action<string>? diagnosticReceived = null);
    Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(CancellationToken cancellationToken);
    void Stop();
}
