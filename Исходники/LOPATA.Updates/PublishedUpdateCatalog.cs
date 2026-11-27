using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lopata.Updates;

public enum UpdateDelivery { FullInstaller, FilePatch }
public sealed record InstallerArtifact(string FileName, string Url, long Size, string Sha256);
public sealed record PublishedUpdateVersion(string Version, string Tag);
public sealed record UpdateOffer(string Version, UpdateDelivery Delivery, UpdateNote[] Notes,
    InstallerArtifact? Installer, SignedManifest? SignedFiles, UpdateManifest? Files, bool NotesIncomplete = false)
{
    public PublishedUpdateVersion[] PublishedVersions { get; init; } = [];
}

/// <summary>A published release identified from the catalogue, with its downloadable metadata still unchecked.</summary>
public sealed class PublishedUpdateCandidate
{
    internal PublishedUpdateCandidate(PublishedUpdateCatalog.ReleaseSelection selection, UpdateDelivery delivery, UpdateNote[] notes)
    {
        Selection = selection;
        Delivery = delivery;
        Notes = notes;
    }

    internal PublishedUpdateCatalog.ReleaseSelection Selection { get; }
    public string Version => Selection.Selected.Version;
    public UpdateDelivery Delivery { get; }
    public UpdateNote[] Notes { get; }
    public bool NotesIncomplete => !Selection.FeedComplete || Notes.Any(note => string.IsNullOrWhiteSpace(note.Text));
}

public sealed class PublishedUpdateCatalog(HttpClient http, IReadOnlyDictionary<string, string> publicKeys)
{
    public const string Repository = "PiTrolKun/LOPATA";
    public const string FileManifestName = "lopata-files.json";

    internal sealed record ReleaseEntry(JsonElement Release, string Version);
    internal sealed record ReleaseSelection(ReleaseEntry Selected, ReleaseEntry[] Releases, Version CurrentVersion,
        bool FeedComplete, string ManifestName, bool PreferFreshManifest);

    public async Task<UpdateOffer?> CheckAsync(string currentVersion, UpdateDelivery delivery, CancellationToken token = default,
        bool requireSetup = false, bool preferFreshManifest = false)
    {
        var current = ParseCurrentVersion(currentVersion);
        var preferFresh = requireSetup || preferFreshManifest;
        var (releases, feedComplete) = await ReadFeedAsync(current, delivery, preferFresh, null, token);
        foreach (var entry in releases)
        {
            var candidate = CreateCandidate(entry, releases, current, feedComplete, delivery, preferFresh);
            if (candidate is null) continue;
            var offer = await ResolveAsync(candidate, token);
            if (requireSetup && offer.Files is { } manifest
                && !manifest.Files.Any(f => f.Root == "app" && f.Path == BootstrapPreparation.ProtocolFile)) continue;
            return offer;
        }
        return null;
    }

    public async Task<PublishedUpdateCandidate?> DiscoverAsync(string currentVersion, UpdateDelivery delivery,
        CancellationToken token = default, bool preferFreshManifest = false,
        Action<PublishedUpdateCandidate>? onFirstCandidate = null)
    {
        var current = ParseCurrentVersion(currentVersion);
        var (releases, feedComplete) = await ReadFeedAsync(current, delivery, preferFreshManifest, onFirstCandidate, token);
        foreach (var entry in releases)
        {
            var candidate = CreateCandidate(entry, releases, current, feedComplete, delivery, preferFreshManifest);
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static Version ParseCurrentVersion(string version) =>
        UpdateManifest.NumericVersion(version.EndsWith("-dev", StringComparison.Ordinal) ? version[..^4] : version);

    private async Task<(ReleaseEntry[] Releases, bool Complete)> ReadFeedAsync(Version current, UpdateDelivery delivery,
        bool preferFreshManifest, Action<PublishedUpdateCandidate>? onFirstCandidate, CancellationToken token)
    {
        var releases = new List<ReleaseEntry>();
        var feedComplete = false;
        // Announce the first eligible item while the feed is streaming. Select by version after the full scan.
        for (var page = 1; page <= 10; page++)
        {
            var count = 0;
            await foreach (var release in ReadReleasePageAsync(http, page, token))
            {
                count++;
                if (!release.GetProperty("draft").GetBoolean()
                    && VersionFromTag(release.GetProperty("tag_name").GetString()) is { } version
                    && UpdateManifest.NumericVersion(version) > current)
                {
                    var entry = new ReleaseEntry(release, version);
                    releases.Add(entry);
                    if (onFirstCandidate is not null && CreateCandidate(entry, [entry], current, false, delivery,
                            preferFreshManifest) is { } announcement)
                    {
                        onFirstCandidate(announcement);
                        onFirstCandidate = null;
                    }
                }
            }
            if (count < 100) { feedComplete = true; break; }
        }
        return (releases.OrderByDescending(r => UpdateManifest.NumericVersion(r.Version)).ToArray(), feedComplete);
    }

    private static PublishedUpdateCandidate? CreateCandidate(ReleaseEntry entry, ReleaseEntry[] releases,
        Version current, bool feedComplete, UpdateDelivery delivery, bool preferFreshManifest)
    {
        var manifestName = preferFreshManifest && Asset(entry.Release, EngineSetTransition.FreshManifestName) is not null
            ? EngineSetTransition.FreshManifestName : FileManifestName;
        if (delivery == UpdateDelivery.FilePatch && Asset(entry.Release, manifestName) is null) return null;
        if (delivery == UpdateDelivery.FullInstaller && !HasInstaller(entry)) return null;
        var upper = UpdateManifest.NumericVersion(entry.Version);
        var applicable = releases.Where(r => UpdateManifest.NumericVersion(r.Version) <= upper).ToArray();
        var notes = applicable.Select(r => new UpdateNote(r.Version,
                r.Release.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue,
                r.Release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : ""))
            .OrderBy(note => UpdateManifest.NumericVersion(note.Version)).ToArray();
        return new PublishedUpdateCandidate(new ReleaseSelection(entry, applicable, current, feedComplete,
            manifestName, preferFreshManifest), delivery, notes);
    }

    public async Task<UpdateOffer> ResolveAsync(PublishedUpdateCandidate candidate, CancellationToken token = default)
    {
        var selection = candidate.Selection;
        var release = selection.Selected.Release;
        var version = candidate.Version;
        var tag = release.GetProperty("tag_name").GetString()!;
        SignedManifest? envelope = null;
        UpdateManifest? files = null;
        if (Asset(release, selection.ManifestName) is { } asset)
        {
            var bytes = await ReadBytesAsync(AssetUrl(asset, tag, selection.ManifestName), SignedManifest.MaximumEnvelopeBytes, token);
            envelope = SignedManifest.Read(bytes);
            files = envelope.Verify(publicKeys);
            if (files.Version != version) throw new InvalidDataException("Release tag and signed manifest differ.");
        }

        var installer = candidate.Delivery == UpdateDelivery.FullInstaller
            ? await ReadInstallerAsync(release, version, tag, token) : null;
        var notes = files?.Notes.Where(n => UpdateManifest.NumericVersion(n.Version) > selection.CurrentVersion)
            .OrderBy(n => UpdateManifest.NumericVersion(n.Version)).ToArray() ?? candidate.Notes;
        var missing = notes.Any(n => string.IsNullOrWhiteSpace(n.Text))
            || (files is null && !selection.FeedComplete)
            || selection.Releases.Any(c => !notes.Any(n => n.Version == c.Version));
        var published = selection.Releases
            .Where(c => candidate.Delivery == UpdateDelivery.FilePatch
                ? Asset(c.Release, FileManifestName) is not null ||
                  (selection.PreferFreshManifest && Asset(c.Release, EngineSetTransition.FreshManifestName) is not null)
                : HasInstaller(c))
            .Select(c => new PublishedUpdateVersion(c.Version, c.Release.GetProperty("tag_name").GetString()!))
            .DistinctBy(c => UpdateManifest.NumericVersion(c.Version)).ToArray();
        return new(version, candidate.Delivery, notes, installer, envelope, files, missing) { PublishedVersions = published };
    }

    private static bool HasInstaller(ReleaseEntry release) => Asset(release.Release, $"LOPATA_Setup_{release.Version}.exe") is not null
        && Asset(release.Release, "lopata-update.json") is not null;

    private async Task<InstallerArtifact> ReadInstallerAsync(JsonElement release, string version, string tag, CancellationToken token)
    {
        var installerName = $"LOPATA_Setup_{version}.exe";
        var installer = Asset(release, installerName)!.Value;
        var installerMetadata = Asset(release, "lopata-update.json")!.Value;
        using var metadata = await ReadJsonAsync(AssetUrl(installerMetadata, tag, "lopata-update.json"), token);
        var root = metadata.RootElement;
        var size = root.GetProperty("size").GetInt64();
        var hash = root.GetProperty("sha256").GetString()!.ToLowerInvariant();
        if (root.GetProperty("version").GetString() != version || root.GetProperty("fileName").GetString() != installerName
            || size <= 0 || size >= 2L * 1024 * 1024 * 1024 || size != installer.GetProperty("size").GetInt64())
            throw new InvalidDataException("Installer metadata does not match release.");
        UpdateManifest.ValidateHash(hash);
        return new(installerName, AssetUrl(installer, tag, installerName), size, hash);
    }

    public static bool IsAssetUrl(string url, string tag, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == $"/{Repository}/releases/download/{tag}/{name}";

    public static string? VersionFromTag(string? tag)
    {
        var value = tag?.StartsWith('v') == true ? tag[1..] : tag;
        return value is not null && Regex.IsMatch(value, @"^\d{1,8}\.\d{1,8}\.\d{1,8}(-beta)?$") ? value : null;
    }

    private static JsonElement? Asset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        var matches = assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Duplicate release asset.");
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string AssetUrl(JsonElement asset, string tag, string name)
    {
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!IsAssetUrl(url, tag, name)) throw new InvalidDataException("Untrusted release asset URL.");
        return url;
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token) =>
        JsonDocument.Parse(await ReadBytesAsync(url, 4 * 1024 * 1024, token));

    public static async IAsyncEnumerable<JsonElement> ReadReleasePageAsync(HttpClient client, int page,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
        request.Headers.UserAgent.ParseAdd("LOPATA-Updater/2.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await foreach (var release in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(input, cancellationToken: token))
            yield return release;
    }

    private async Task<byte[]> ReadBytesAsync(string url, int maximumBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LOPATA-Updater/2.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Release metadata is too large.");
        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Release metadata exceeded size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
