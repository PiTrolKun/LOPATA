namespace AIHub.Models;

public sealed record MusicCoverSelection(string Path, string Sha256, string Mime)
{
    // Every cover supported before provenance was introduced used Z-Image Turbo.
    public string ModelName { get; init; } = "Z-Image Turbo";
}
public sealed record MusicCoverEntry(string Artist, string Title)
{
    public bool? Enabled { get; init; }
    public MusicCoverSelection? Selected { get; init; }
    public string? LastRunId { get; init; }
    public bool IsEnabled => Enabled ?? (Artist.Length > 0 && Title.Length > 0);
}
public sealed record MusicCoverInput(string Id, string Artist, string Title, string Lyrics, string Style,
    string ModelsRoot, string ModelPath, string? JobId = null, string? PromptOverride = null);
public sealed record MusicCoverRun(MusicCoverInput Input)
{
    public string Summary { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Stage { get; init; } = "Summary";
    public ImageGenerationRequest? Images { get; init; }
    public string[] Ready { get; init; } = [];
    public string? Error { get; init; }
}
