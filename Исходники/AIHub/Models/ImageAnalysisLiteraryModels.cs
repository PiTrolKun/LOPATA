namespace AIHub.Models;

public static class ImageAnalysisLiteraryStatuses
{
    public const string Draft = "draft";
    public const string FileReady = "file_ready";
    public const string AnalysingVision = "analysing_vision";
    public const string Writing = "writing";
    public const string ResultReady = "result_ready";
    public const string Revising = "revising";
    public const string Failed = "failed";
    public const string Completed = "completed";
}

public static class ImageAnalysisLiterarySteps
{
    public const string Subscenario = "subscenario";
    public const string Image = "image";
    public const string Settings = "settings";
    public const string Result = "result";
}

public static class ImageAnalysisEventStatuses
{
    public const string Completed = "completed";
    public const string Active = "active";
    public const string Failed = "failed";
}

public static class ImageAnalysisEventCodes
{
    public const string FileCheckStarted = "FileCheckStarted";
    public const string FileReady = "FileReady";
    public const string FileRejected = "FileRejected";
    public const string VisionStarted = "VisionStarted";
    public const string VisionCompleted = "VisionCompleted";
    public const string CoreStarted = "CoreStarted";
    public const string DescriptionReady = "DescriptionReady";
    public const string RevisionStarted = "RevisionStarted";
    public const string RevisionReady = "RevisionReady";
    public const string OperationFailed = "OperationFailed";
    public const string ExportCompleted = "ExportCompleted";
    public const string SessionCompleted = "SessionCompleted";
}

public static class ImageAnalysisAccuracyModes
{
    public const string Strict = "strict";
    public const string Balanced = "balanced";
    public const string Free = "free";
}

public static class ImageAnalysisLiteraryStyles
{
    public const string Neutral = "neutral";
    public const string Atmospheric = "atmospheric";
    public const string Dramatic = "dramatic";
    public const string FairyTale = "fairy_tale";
}

public static class ImageAnalysisTextLengths
{
    public const string Brief = "brief";
    public const string Standard = "standard";
    public const string Detailed = "detailed";
}

public static class ImageAnalysisTextForms
{
    public const string Continuous = "continuous";
    public const string WithTitle = "with_title";
}

public sealed class ImageAnalysisLiterarySettings
{
    public string LanguageCode { get; set; } = "ru";

    public string Accuracy { get; set; } = ImageAnalysisAccuracyModes.Balanced;

    public string Style { get; set; } = ImageAnalysisLiteraryStyles.Atmospheric;

    public string Length { get; set; } = ImageAnalysisTextLengths.Standard;

    public string Form { get; set; } = ImageAnalysisTextForms.WithTitle;

    public string Wishes { get; set; } = string.Empty;

    public string PromptMode { get; set; } = PromptModes.Standard;

    // Immutable snapshot: changes to the preset library must not rewrite a saved work.
    public PromptPairPreset? CustomPrompts { get; set; }
}

public sealed class ImageAnalysisFilePassport
{
    public string SourcePath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset LastWriteTimeUtc { get; set; }
}

public sealed class ImageAnalysisLiteraryVersion
{
    public string VersionId { get; set; } = Guid.NewGuid().ToString("N");

    public int Number { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string Text { get; set; } = string.Empty;

    public string ChangeRequest { get; set; } = string.Empty;

    public string Source { get; set; } = "initial";
}

public sealed class ImageAnalysisEventEntry
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string Code { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Status { get; set; } = ImageAnalysisEventStatuses.Completed;

    public string Detail { get; set; } = string.Empty;
}

public sealed class ImageAnalysisReviewSummary
{
    public List<string> Items { get; set; } = [];

    public List<string> Uncertainties { get; set; } = [];
}

public sealed class ImageAnalysisLiterarySession
{
    public int SchemaVersion { get; set; } = 3;

    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    public string ScenarioId { get; set; } = "image_analysis";

    public string SubscenarioId { get; set; } = "literary_single_image";

    public string BundleId { get; set; } = "medium";

    public string PipelineId { get; set; } = ImageAnalysisPipelineIds.Legacy;

    public string PipelineVersion { get; set; } = ImageAnalysisPipelineIds.LegacyVersion;

    public string ContractVersion { get; set; } = ImageAnalysisPipelineIds.ContractVersion;

    public string ModelId { get; set; } = string.Empty;

    public string ModelRevision { get; set; } = string.Empty;

    public string RuntimeId { get; set; } = ImageAnalysisRuntimeIds.Legacy;

    public string RuntimeVersion { get; set; } = string.Empty;

    public string Status { get; set; } = ImageAnalysisLiteraryStatuses.Draft;

    public string CurrentStep { get; set; } = ImageAnalysisLiterarySteps.Subscenario;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public ImageAnalysisFilePassport? File { get; set; }

    public ImageAnalysisLiterarySettings Settings { get; set; } = new();

    public string VisualReport { get; set; } = string.Empty;

    public List<ImageAnalysisHiddenMessage> HiddenConversation { get; set; } = [];

    public bool ContextBlocked { get; set; }

    public string AnalysisLanguageCode { get; set; } = string.Empty;

    public ImageAnalysisPlacementInfo Placement { get; set; } = new();

    public ImageAnalysisRuntimeMetrics RuntimeMetrics { get; set; } = new();

    public ImageAnalysisSpeechResult SpeechResult { get; set; } = new();

    public List<string> Observations { get; set; } = [];

    public ImageAnalysisReviewSummary ReviewSummary { get; set; } = new();

    public List<ImageAnalysisEventEntry> Events { get; set; } = [];

    public List<ImageAnalysisLiteraryVersion> Versions { get; set; } = [];

    public string SelectedVersionId { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public string InternalImageCopyPath { get; set; } = string.Empty;

    public string InternalDescriptionCopyPath { get; set; } = string.Empty;

    public List<string> ExportedFiles { get; set; } = [];

    public ImageAnalysisLiteraryVersion? GetSelectedVersion() =>
        Versions.FirstOrDefault(version => string.Equals(
            version.VersionId,
            SelectedVersionId,
            StringComparison.Ordinal))
        ?? Versions.LastOrDefault();
}

public sealed record ImageAnalysisLiteraryProgress(
    string Role,
    string Stage,
    string Message);

public sealed record ImageAnalysisLiteraryResult(
    string VisualReport,
    string Description,
    ImageAnalysisReviewSummary ReviewSummary,
    IReadOnlyList<ImageAnalysisHiddenMessage>? HiddenConversation = null,
    string RawFinalResponse = "",
    long VisualPassMilliseconds = 0,
    long ComposePassMilliseconds = 0);
