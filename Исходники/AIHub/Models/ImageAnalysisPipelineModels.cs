namespace AIHub.Models;

public static class ImageAnalysisPipelineIds
{
    public const string OmniAlpha = "omni_alpha_gguf_single_image_literary";
    public const string OmniAlphaVersion = "2";
    public const string OmniBeta = "omni_beta_gguf_single_image_literary";
    public const string OmniBetaVersion = "1";
    public const string Legacy = "legacy_single_image_literary";
    public const string OmniHeavy = "omni_heavy_single_image_literary";
    public const string ContractVersion = "single-image-literary/3";
    public const string LegacyVersion = "1";
    public const string OmniHeavyVersion = "2";
}

public static class ImageAnalysisRuntimeIds
{
    public const string OmniLlama = "qwen2_5_omni_llama_cpp";
    public const string Qwen35Llama = "qwen35_llama_cpp";
    public const string OmniBeta = "omni_beta_gguf_single_image_literary";
    public const string OmniBetaVersion = "1";
    public const string Legacy = "legacy_kimi_core";
    public const string Qwen25OmniTransformers = "qwen2_5_omni_transformers_worker";
}

public sealed class ImageAnalysisHiddenMessage
{
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public bool IncludesImage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record ImageAnalysisPipelineCheckpoint(
    string VisualReport,
    IReadOnlyList<ImageAnalysisHiddenMessage> HiddenConversation);

public sealed class ImageAnalysisPlacementInfo
{
    public string Strategy { get; set; } = string.Empty;

    public long GpuBudgetBytes { get; set; }

    public long CpuBudgetBytes { get; set; }

    public long AvailableVramBytes { get; set; }

    public long AvailableRamBytes { get; set; }

    public long CommitAvailableBytes { get; set; }

    public string DeviceMapJson { get; set; } = string.Empty;

    public DateTimeOffset CalculatedAt { get; set; }
}

public sealed class ImageAnalysisRuntimeMetrics
{
    public long WarmupMilliseconds { get; set; }

    public long VisualPassMilliseconds { get; set; }

    public long ComposePassMilliseconds { get; set; }

    public long SpeechMilliseconds { get; set; }

    public long TimeToFirstAudioMilliseconds { get; set; }

    public long PeakWorkingSetBytes { get; set; }

    public long RamBeforeWarmupBytes { get; set; }

    public long RamAfterWarmupBytes { get; set; }

    public long CommitBeforeWarmupBytes { get; set; }

    public long CommitAfterWarmupBytes { get; set; }

    public long VramBeforeWarmupBytes { get; set; }

    public long VramAfterWarmupBytes { get; set; }
}

public sealed class ImageAnalysisSpeechResult
{
    public string RequestedMode { get; set; } = ImageAnalysisSpeechModes.Off;

    public string ActualProvider { get; set; } = string.Empty;

    public string Speaker { get; set; } = string.Empty;

    public int Volume { get; set; }

    public int RatePercent { get; set; }

    public string TemporaryAudioPath { get; set; } = string.Empty;

    public long SynthesisMilliseconds { get; set; }

    public long TimeToFirstAudioMilliseconds { get; set; }

    public bool Completed { get; set; }

    public bool Cancelled { get; set; }

    public bool AutomaticFallbackUsed { get; set; }

    public string Error { get; set; } = string.Empty;
}
