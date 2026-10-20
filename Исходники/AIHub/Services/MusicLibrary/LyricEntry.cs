using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services.MusicLibrary;

public sealed record LyricEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Revision { get; init; }
    public string? PreviousVersionId { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Language { get; init; } = "und";
    public string Genre { get; init; } = "";
    public string Style { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Text { get; init; } = "";
    public string Origin { get; init; } = "manual; model history unknown";
    public OutputModelUse[] Models { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string TextHash => Hash(Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    public override string ToString() => Title;
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || Revision < 0 || string.IsNullOrWhiteSpace(Title)
            || Title.Length > 500 || string.IsNullOrWhiteSpace(Text) || Text.Length > 2_000_000
            || new[] { Author, Artist, Genre, Style, Tags, Notes, Origin }.Any(s => s is null || s.Length > 8000)
            || Language is null || !Regex.IsMatch(Language, @"\A[a-z]{2,3}(?:-[A-Za-z]{2,8})?\z")
            || Models is null || Models.Length > 100
            || PreviousVersionId is not null && !Guid.TryParseExact(PreviousVersionId, "N", out _))
            throw new InvalidDataException("Music.Library.InvalidRecord");
        _ = new OutputProvenance { Models = Models }.Json();
    }
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed record LyricSaveResult(LyricEntry Entry, bool Duplicate);
public sealed record LyricLibrarySnapshot(long Generation, LyricEntry[] Entries);
public sealed record LyricExample(string Id, int Revision, string Title, string Section, string Text, double Score)
{
    public string Artist { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Style { get; init; } = "";
    public string Language { get; init; } = "und";
    public string MatchKind { get; init; } = "semantic";
}
