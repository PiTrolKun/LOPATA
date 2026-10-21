namespace AIHub.Models;

public enum MusicKaraokeReadiness { Pending, Ready, Unavailable }

public sealed record MusicTrack(string? Path, string Title, TimeSpan Duration, DateTime CreatedAt)
{
    public bool IsExample => string.IsNullOrEmpty(Path);
    public string? JobId { get; init; }
    public int Variant { get; init; }
    public string? AdditionalPath { get; init; }
    public string? BundleFolder { get; init; }
    public string? MusicPath { get; init; }
    public string? AdditionalMusicPath { get; init; }
    public bool IsLayered => BundleFolder is not null && MusicPath is not null;
    public MusicKaraokeReadiness? KaraokeReadiness { get; init; }
    public bool KaraokeUnavailable => KaraokeReadiness == MusicKaraokeReadiness.Unavailable;
}
