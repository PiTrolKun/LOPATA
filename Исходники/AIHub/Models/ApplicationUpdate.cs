using System.Text.RegularExpressions;

namespace AIHub.Models;

public sealed class ApplicationUpdateSettings
{
    public bool CheckOnStartup { get; set; } = true;
    public bool? IncludeBeta { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
}

public sealed record ApplicationReleaseVersion(int Major, int Minor, int Patch, string Channel)
    : IComparable<ApplicationReleaseVersion>
{
    public static ApplicationReleaseVersion? Parse(string? text)
    {
        var match = Regex.Match(text ?? "", @"^v?(\d{1,8})\.(\d{1,8})\.(\d{1,8})(?:-(dev|beta))?$", RegexOptions.CultureInvariant);
        return match.Success
            ? new(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value), match.Groups[4].Value)
            : null;
    }

    public int CompareTo(ApplicationReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        return result == 0 ? Rank(Channel).CompareTo(Rank(other.Channel)) : result;
    }

    private static int Rank(string channel) => channel switch { "dev" => 0, "beta" => 1, _ => 2 };
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Channel.Length == 0 ? "" : $"-{Channel}");
}

public sealed record ApplicationUpdate(
    string Version, string FileName, string DownloadUrl, long Size, string Sha256, string Notes);
