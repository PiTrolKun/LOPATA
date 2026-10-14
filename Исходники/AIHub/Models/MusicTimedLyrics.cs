using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIHub.Models;

public sealed record MusicTimedLyricsWord(int SourceStart, int SourceLength, double StartSeconds, double EndSeconds);
public sealed record MusicTimedLyricsClock(string SourcePayloadHash, double OffsetSeconds, double MinimumCorrelation, double LagSpreadSeconds);
public sealed record MusicTimedLyricsLine(int SourceStart, int SourceLength, double StartSeconds, double EndSeconds)
{
    public double? Confidence { get; init; }
    public MusicTimedLyricsWord[] Words { get; init; } = [];
}

/// <summary>Offsets refer to the exact source with CRLF/CR canonicalized to LF; occurrences are never deduplicated.</summary>
public sealed record MusicTimedLyrics
{
    public int Schema { get; init; } = 1;
    public required string JobId { get; init; }
    public required int Variant { get; init; }
    public string? ProjectId { get; init; }
    public int? ProjectStep { get; init; }
    public required string SourceLyrics { get; init; }
    public required string LyricsHash { get; init; }
    public required string AudioPayloadHash { get; init; }
    public required double DurationSeconds { get; init; }
    public required string Provider { get; init; }
    public required string ProviderVersion { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Accuracy { get; init; } = "line";
    public MusicTimedLyricsLine[] Lines { get; init; } = [];
    public MusicTimedLyricsClock? Clock { get; init; }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    public static string HashLyrics(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
    public void Validate()
    {
        if (Schema != 1 || !Guid.TryParseExact(JobId, "N", out _) || Variant is < 0 or > 7 ||
            ProjectId is not null && (!Guid.TryParseExact(ProjectId, "N", out _) || ProjectStep is null or < 1) ||
            ProjectId is null && ProjectStep is not null || SourceLyrics is null || SourceLyrics != Normalize(SourceLyrics) ||
            !string.Equals(LyricsHash, HashLyrics(SourceLyrics), StringComparison.OrdinalIgnoreCase) ||
            AudioPayloadHash is null || AudioPayloadHash.Length != 64 || !AudioPayloadHash.All(Uri.IsHexDigit) ||
            !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 || string.IsNullOrWhiteSpace(Provider) ||
            string.IsNullOrWhiteSpace(ProviderVersion) || Accuracy is not ("line" or "word" or "approximate") || Lines is null || Lines.Length > 10000)
            throw new InvalidDataException("Invalid timed lyrics identity.");
        if (Clock is { } clock && (clock.SourcePayloadHash is null || clock.SourcePayloadHash.Length != 64 || !clock.SourcePayloadHash.All(Uri.IsHexDigit) ||
            !double.IsFinite(clock.OffsetSeconds) || Math.Abs(clock.OffsetSeconds) > 1 || !double.IsFinite(clock.MinimumCorrelation) ||
            clock.MinimumCorrelation is < .9 or > 1.001 || !double.IsFinite(clock.LagSpreadSeconds) || clock.LagSpreadSeconds is < 0 or > .03))
            throw new InvalidDataException("Invalid audio clock comparison.");
        var previousEnd = 0d; var previousSourceEnd = 0;
        foreach (var line in Lines)
        {
            if (line is null) throw new InvalidDataException("Missing timed lyrics occurrence.");
            Range(line.SourceStart, line.SourceLength, line.StartSeconds, line.EndSeconds, DurationSeconds);
            if (line.StartSeconds < previousEnd || line.SourceStart < previousSourceEnd ||
                string.IsNullOrWhiteSpace(SourceLyrics.Substring(line.SourceStart, line.SourceLength)) ||
                line.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1) || line.Words is null)
                throw new InvalidDataException("Invalid timed lyrics occurrence order.");
            var wordEnd = line.StartSeconds; var sourceEnd = line.SourceStart;
            foreach (var word in line.Words)
            {
                if (word is null) throw new InvalidDataException("Missing timed lyrics word.");
                Range(word.SourceStart, word.SourceLength, word.StartSeconds, word.EndSeconds, line.EndSeconds);
                if (word.SourceStart < sourceEnd || word.SourceStart + word.SourceLength > line.SourceStart + line.SourceLength || word.StartSeconds < wordEnd)
                    throw new InvalidDataException("Invalid timed lyrics word order.");
                wordEnd = word.EndSeconds; sourceEnd = word.SourceStart + word.SourceLength;
            }
            previousEnd = line.EndSeconds; previousSourceEnd = line.SourceStart + line.SourceLength;
        }
        void Range(int start, int length, double from, double to, double duration)
        {
            if (start < 0 || length < 1 || start > SourceLyrics.Length - length || !double.IsFinite(from) ||
                !double.IsFinite(to) || from < 0 || to <= from || to > duration + .001)
                throw new InvalidDataException("Invalid timed lyrics interval.");
        }
    }
    public MusicTimedLyricsLine? FindLine(double seconds) => double.IsFinite(seconds)
        ? Lines.FirstOrDefault(line => line.StartSeconds <= seconds && seconds < line.EndSeconds) : null;
}
