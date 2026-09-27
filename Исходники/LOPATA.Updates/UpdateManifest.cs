using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lopata.Updates;

public sealed record UpdateFile(string Root, string Path, long Size, string Sha256, string Package)
{
    [JsonIgnore] public string Key => Root + "/" + Path;
}

public sealed record UpdatePackage(string Id, string Url, long Size, string Sha256);
public sealed record UpdateNote(string Version, DateTimeOffset PublishedUtc, string Text);

public sealed record UpdateManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Product { get; init; } = "LOPATA";
    public string Version { get; init; } = "";
    public string SourceCommit { get; init; } = "";
    public int MinimumUpdaterVersion { get; init; } = 1;
    public UpdateFile[] Files { get; init; } = [];
    public UpdatePackage[] Packages { get; init; } = [];
    public UpdateNote[] Notes { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        WriteIndented = true
    };

    public static Version NumericVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^\d{1,8}\.\d{1,8}\.\d{1,8}(-beta)?$"))
            throw new InvalidDataException("Invalid public release version.");
        return System.Version.Parse(value.Split('-')[0]);
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Product != "LOPATA" || MinimumUpdaterVersion != 1
            || !Regex.IsMatch(SourceCommit ?? "", "^[a-f0-9]{40}$"))
            throw new InvalidDataException("Unsupported update manifest.");
        _ = NumericVersion(Version);
        if (Files is null || Files.Length is 0 or > 100000 || Packages is null
            || Packages.Length is 0 or > 950 || Notes is null || Notes.Length > 10000)
            throw new InvalidDataException("Invalid manifest collection sizes.");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in Packages)
        {
            if (package is null || package.Id is null || !Regex.IsMatch(package.Id, "^[a-z0-9-]{1,100}\\.zip$")
                || !packages.Add(package.Id) || package.Size <= 0 || package.Size >= 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid or duplicate package.");
            ValidateHash(package.Sha256);
            if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || !Regex.IsMatch(uri.AbsolutePath, @"^/PiTrolKun/LOPATA/releases/download/v\d{1,8}\.\d{1,8}\.\d{1,8}(-beta)?/" + Regex.Escape(package.Id) + "$"))
                throw new InvalidDataException("Package URL is outside the release repository.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || file.Root is not ("app" or "llama" or "chatllm")
                || file.Size < 0 || file.Size > 32L * 1024 * 1024 * 1024
                || !packages.Contains(file.Package) || !names.Add(file.Key))
                throw new InvalidDataException("Invalid or duplicate file.");
            SafeUpdatePath.ValidateRelative(file.Path);
            ValidateHash(file.Sha256);
            usedPackages.Add(file.Package);
        }
        foreach (var file in Files)
        {
            var parent = file.Key;
            while (parent.LastIndexOf('/') is var slash && slash > 0)
            {
                parent = parent[..slash];
                if (names.Contains(parent)) throw new InvalidDataException("File/directory collision in manifest.");
            }
        }
        if (usedPackages.Count != packages.Count) throw new InvalidDataException("Unreferenced update package.");
        var noteVersions = new HashSet<Version>();
        foreach (var note in Notes)
        {
            if (note is null || string.IsNullOrWhiteSpace(note.Text) || note.Text.Length > 100000
                || note.PublishedUtc == default || NumericVersion(note.Version) > NumericVersion(Version)
                || !noteVersions.Add(NumericVersion(note.Version)))
                throw new InvalidDataException("Invalid release history.");
        }
        if (!noteVersions.Contains(NumericVersion(Version))) throw new InvalidDataException("Release notes are required.");
    }

    public static void ValidateHash(string hash)
    {
        if (!Regex.IsMatch(hash ?? "", "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid SHA-256.");
    }
}
