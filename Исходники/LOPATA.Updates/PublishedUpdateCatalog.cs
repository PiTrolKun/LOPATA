using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lopata.Updates;

public enum UpdateDelivery { FullInstaller, FilePatch }
public sealed record InstallerArtifact(string FileName, string Url, long Size, string Sha256);
public sealed record UpdateOffer(string Version, UpdateDelivery Delivery, UpdateNote[] Notes,
    InstallerArtifact? Installer, SignedManifest? SignedFiles, UpdateManifest? Files, bool NotesIncomplete = false);

public sealed class PublishedUpdateCatalog(HttpClient http, IReadOnlyDictionary<string, string> publicKeys)
{
    public const string Repository = "PiTrolKun/LOPATA";
    public const string FileManifestName = "lopata-files.json";

    public async Task<UpdateOffer?> CheckAsync(string currentVersion, UpdateDelivery delivery, CancellationToken token = default)
    {
        var current = UpdateManifest.NumericVersion(currentVersion.EndsWith("-dev", StringComparison.Ordinal)
            ? currentVersion[..^4] : currentVersion);
        var releases = new List<JsonElement>();
        var feedComplete = false;
        // GitHub returns newest publication first, not numeric version order. Collect before selecting.
        for (var page = 1; page <= 10; page++)
        {
            using var data = await ReadJsonAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}", token);
            if (data.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid release feed.");
            releases.AddRange(data.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean()).Select(r => r.Clone()));
            if (data.RootElement.GetArrayLength() < 100) { feedComplete = true; break; }
        }
        var candidates = releases.Select(r => (Release: r, Version: VersionFromTag(r.GetProperty("tag_name").GetString())))
            .Where(r => r.Version is not null && UpdateManifest.NumericVersion(r.Version) > current)
            .OrderByDescending(r => UpdateManifest.NumericVersion(r.Version!)).ToArray();
        foreach (var (release, versionOrNull) in candidates)
        {
            var version = versionOrNull!;
            var tag = release.GetProperty("tag_name").GetString()!;
            var signedAsset = Asset(release, FileManifestName);
            var installerName = $"LOPATA_Setup_{version}.exe";
            var installer = Asset(release, installerName);
            var installerMetadata = Asset(release, "lopata-update.json");
            if (delivery == UpdateDelivery.FilePatch && signedAsset is null) continue;
            if (delivery == UpdateDelivery.FullInstaller && (installer is null || installerMetadata is null)) continue;
            SignedManifest? envelope = null;
            UpdateManifest? files = null;
            if (signedAsset is { } asset)
            {
                var bytes = await ReadBytesAsync(AssetUrl(asset, tag, FileManifestName), SignedManifest.MaximumEnvelopeBytes, token);
                envelope = SignedManifest.Read(bytes);
                files = envelope.Verify(publicKeys);
                if (files.Version != version) throw new InvalidDataException("Release tag and signed manifest differ.");
            }
            InstallerArtifact? artifact = null;
            if (delivery == UpdateDelivery.FullInstaller)
            {
                using var metadata = await ReadJsonAsync(AssetUrl(installerMetadata!.Value, tag, "lopata-update.json"), token);
                var root = metadata.RootElement;
                var size = root.GetProperty("size").GetInt64();
                var hash = root.GetProperty("sha256").GetString()!;
                if (root.GetProperty("version").GetString() != version || root.GetProperty("fileName").GetString() != installerName
                    || size <= 0 || size >= 2L * 1024 * 1024 * 1024 || size != installer!.Value.GetProperty("size").GetInt64())
                    throw new InvalidDataException("Installer metadata does not match release.");
                UpdateManifest.ValidateHash(hash.ToLowerInvariant());
                artifact = new(installerName, AssetUrl(installer.Value, tag, installerName), size, hash.ToLowerInvariant());
            }
            var notes = files?.Notes.Where(n => UpdateManifest.NumericVersion(n.Version) > current).ToArray()
                ?? candidates.Where(c => UpdateManifest.NumericVersion(c.Version!) <= UpdateManifest.NumericVersion(version))
                    .Select(c => new UpdateNote(c.Version!,
                        c.Release.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue,
                        c.Release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "")).ToArray();
            var missing = notes.Any(n => string.IsNullOrWhiteSpace(n.Text))
                || (files is null && !feedComplete)
                || candidates.Where(c => UpdateManifest.NumericVersion(c.Version!) <= UpdateManifest.NumericVersion(version))
                    .Any(c => !notes.Any(n => n.Version == c.Version));
            return new(version, delivery, notes.OrderBy(n => UpdateManifest.NumericVersion(n.Version)).ToArray(), artifact, envelope, files, missing);
        }
        return null;
    }

    public static bool IsAssetUrl(string url, string tag, string name) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com"
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == $"/{Repository}/releases/download/{tag}/{name}";

    private static string? VersionFromTag(string? tag)
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
