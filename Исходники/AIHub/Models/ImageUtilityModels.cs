namespace AIHub.Models;

public enum ImageUtilityItemStatus { Pending, Running, Completed, Failed, Duplicate, Cancelled, Skipped }

public sealed class ImageUtilityOptions
{
    public string MethodId { get; set; } = "lanczos3";
    public int Preset { get; set; } = 2160;
    public int? ScaleMultiplier { get; set; }
    public bool FormatOnly { get; set; }
    public string Format { get; set; } = "png";
    public int Quality { get; set; } = 95;
    public string BackgroundColor { get; set; } = "#ffffff";
    public bool PreserveTransparency { get; set; } = true;
    public double Sharpen { get; set; }
    public string CustomName { get; set; } = "";
    public string ExportFolder { get; set; } = "";
    public bool IncludeSubfolders { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ImageUtilityItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Source { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RelativeFolder { get; set; } = "";
    public string? LocalPath { get; set; }
    public string? OutputPath { get; set; }
    public string? PlannedOutputPath { get; set; }
    public string? PreparedOutputSha256 { get; set; }
    public ImageUtilityItemStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public string? ErrorKey { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int OutputWidth { get; set; }
    public int OutputHeight { get; set; }
    public int Frames { get; set; } = 1;
    public bool IsUrl => Uri.TryCreate(Source, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http");
}

public sealed class ImageUtilityJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public ImageUtilityOptions Options { get; set; } = new();
    public List<ImageUtilityItem> Items { get; set; } = [];
    public string? OutputFolder { get; set; }
    public bool Finished { get; set; }
}

public sealed class ImageUtilityPreferences
{
    public string FavoriteMethodId { get; set; } = "lanczos3";
    public ImageUtilityOptions Options { get; set; } = new();
    public long LastProcessNumber { get; set; }
}

public sealed record ImageUtilityMethod(string Id, string NameKey, string DescriptionKey, bool IsAi);
public sealed record ImageUtilityFormat(string Id, string Extension, bool SupportsAlpha, bool SupportsAnimation, bool HasQuality);
public sealed record ImageUtilityImageInfo(int Width, int Height, int Frames, string Format);
public sealed record ImageUtilityProgress(string MessageKey, object[]? Arguments = null, double? Fraction = null, string? ItemId = null, bool IsError = false);

public interface IImageUtilityAiProcessor
{
    Task ProcessAsync(string methodId, string inputPath, string outputPngPath,
        IReadOnlyDictionary<string, string> parameters, IProgress<ImageUtilityProgress>? progress,
        CancellationToken token);
}

public interface IImageUtilityProcessor
{
    Task<string> ProcessAsync(ImageUtilityItem item, ImageUtilityOptions options, string outputFolder,
        IProgress<ImageUtilityProgress>? progress = null, CancellationToken token = default, Action? checkpoint = null);
}

public sealed class ImageUtilityException : Exception
{
    public string MessageKey { get; }
    public bool Retryable { get; }
    public ImageUtilityException(string messageKey, string? detail = null, bool retryable = false)
        : base(detail ?? messageKey) { MessageKey = messageKey; Retryable = retryable; }
}
