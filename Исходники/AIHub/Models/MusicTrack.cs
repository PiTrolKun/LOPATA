namespace AIHub.Models;

public sealed record MusicTrack(string? Path, string Title, TimeSpan Duration, DateTime CreatedAt)
{
    public bool IsExample => string.IsNullOrEmpty(Path);
    public string? JobId { get; init; }
    public int Variant { get; init; }
    public string? AdditionalPath { get; init; }
}
