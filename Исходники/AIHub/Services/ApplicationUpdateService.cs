using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ApplicationUpdateService
{
    public const string Repository = "PiTrolKun/LOPATA";
    public const string ManifestName = "lopata-update.json";
    private readonly HttpClient _http;
    private readonly string _directory;

    public ApplicationUpdateService(HttpClient http, string directory)
    {
        _http = http;
        _directory = directory;
    }

    public async Task<ApplicationUpdate?> CheckAsync(string currentVersion, bool includeBeta, CancellationToken token)
    {
        var current = ApplicationReleaseVersion.Parse(currentVersion)
            ?? throw new InvalidDataException("Invalid application version.");
        var candidates = new List<(ApplicationReleaseVersion Version, JsonElement Release)>();
        for (var page = 1; page <= 5; page++)
        {
            using var document = await ReadJsonAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}", token);
            foreach (var release in document.RootElement.EnumerateArray())
            {
                var version = ApplicationReleaseVersion.Parse(release.GetProperty("tag_name").GetString());
                if (version is null || version.Channel == "dev" || release.GetProperty("draft").GetBoolean()
                    || version.CompareTo(current) <= 0
                    || (!includeBeta && (version.Channel == "beta" || release.GetProperty("prerelease").GetBoolean()))) continue;
                candidates.Add((version, release.Clone()));
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }

        foreach (var candidate in candidates.OrderByDescending(item => item.Version))
        {
            var version = candidate.Version.ToString();
            var fileName = $"LOPATA_Setup_{version}.exe";
            var assets = candidate.Release.GetProperty("assets").EnumerateArray().ToArray();
            var installer = assets.FirstOrDefault(item => item.GetProperty("name").GetString() == fileName);
            var manifest = assets.FirstOrDefault(item => item.GetProperty("name").GetString() == ManifestName);
            if (installer.ValueKind == JsonValueKind.Undefined || manifest.ValueKind == JsonValueKind.Undefined) continue;
            var tag = candidate.Release.GetProperty("tag_name").GetString()!;
            var url = installer.GetProperty("browser_download_url").GetString()!;
            var manifestUrl = manifest.GetProperty("browser_download_url").GetString()!;
            if (!IsAssetUrl(url, tag, fileName) || !IsAssetUrl(manifestUrl, tag, ManifestName)) continue;
            using var metadata = await ReadJsonAsync(manifestUrl, token);
            var data = metadata.RootElement;
            if (!data.TryGetProperty("version", out var manifestVersion) || manifestVersion.GetString() != version
                || !data.TryGetProperty("fileName", out var name) || name.GetString() != fileName
                || !data.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes)
                || bytes <= 0 || bytes != installer.GetProperty("size").GetInt64()
                || !data.TryGetProperty("sha256", out var hash) || !Regex.IsMatch(hash.GetString() ?? "", "^[a-fA-F0-9]{64}$")) continue;
            return new(version, fileName, url, bytes, hash.GetString()!,
                candidate.Release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
        }
        return null;
    }

    public static bool IsAssetUrl(string url, string tag, string fileName) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.AbsolutePath == $"/{Repository}/releases/download/{tag}/{fileName}";

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LOPATA-Updater/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, token);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    public async Task<string> DownloadAsync(ApplicationUpdate update, int connections,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        if (ApplicationReleaseVersion.Parse(update.Version) is not { Channel: not "dev" }
            || update.FileName != $"LOPATA_Setup_{update.Version}.exe"
            || update.Size <= 0 || !Regex.IsMatch(update.Sha256, "^[a-fA-F0-9]{64}$")
            || (!IsAssetUrl(update.DownloadUrl, "v" + update.Version, update.FileName)
                && !IsAssetUrl(update.DownloadUrl, update.Version, update.FileName)))
            throw new InvalidDataException("Invalid update artifact.");
        var folder = Path.Combine(_directory, update.Version, update.Sha256.ToLowerInvariant());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, update.FileName);
        await using var lease = new FileStream(Path.Combine(folder, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            if (await VerifyAsync(path, update, token)) return path;
            File.Delete(path);
        }
        var file = new ManagedModelArtifactFile
        {
            RelativePath = update.FileName,
            SourceUrl = update.DownloadUrl,
            SizeBytes = update.Size,
            Sha256 = update.Sha256
        };
        var card = new ManagedModelArtifactCard { ModelArtifactId = "application-update", Files = [file] };
        var downloader = new SegmentedModelFileDownloader(_http) { MaximumParallelConnections = connections };
        var needed = update.Size - SegmentedModelFileDownloader.GetPartialStoredBytes(path, update.Size)
            + downloader.GetAssemblyReserveBytes(path, update.Size) + 16 * 1024 * 1024;
        if (new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace < needed)
            throw new IOException("Not enough free disk space.");
        await downloader.DownloadAsync(card, file, path, 0, progress, token);
        progress?.Report(new("application-update", update.FileName, update.Size, update.Size, 0, "verifying"));
        if (!await VerifyAsync(path + ".part", update, token))
        {
            foreach (var partial in SegmentedModelFileDownloader.GetPartialArtifactPaths(path)) File.Delete(partial);
            throw new InvalidDataException("Installer checksum mismatch. Download again.");
        }
        File.Move(path + ".part", path, true);
        return path;
    }

    public static async Task<bool> VerifyAsync(string path, ApplicationUpdate update, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != update.Size) return false;
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
