using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ExecutorModelInstaller : IDisposable
{
    private const string ManifestFileName = "executor-model.json";
    private const int GgufHeaderProbeBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    public ExecutorModelInstaller(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
    }

    public async Task<ExecutorModelArtifact> InstallAsync(
        ExecutorModelArtifact artifact,
        StorageSettings storageSettings,
        IProgress<ExecutorDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var auditInstallation = ComponentAudit.Installation();
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(storageSettings);
        await ComponentLicenseGate.EnsureAsync(artifact.RepoId, cancellationToken);
        var modelsRoot = storageSettings.Models.Locations
            .Select(value => value.Path?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidOperationException("Models storage is not configured.");
        if (artifact.SizeBytes <= 0 || string.IsNullOrWhiteSpace(artifact.DownloadUrl))
        {
            throw new InvalidOperationException("Executor artifact metadata is incomplete.");
        }
        if (!Uri.TryCreate(artifact.DownloadUrl, UriKind.Absolute, out var sourceUri)
            || sourceUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Executor artifacts must use an absolute HTTPS source.");
        }
        var freeBytes = GetFreeBytes(modelsRoot);
        if (freeBytes >= 0 && freeBytes < artifact.SizeBytes + 512L * 1024 * 1024)
        {
            throw new IOException("Not enough free disk space for the executor model and verification reserve.");
        }

        var directory = Path.Combine(modelsRoot, "Executors", CreateSafeDirectoryName(artifact.RepoId));
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, Path.GetFileName(artifact.FileName));
        var partialPath = targetPath + ".part";
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (await TryUseVerifiedLocalFileAsync(
                artifact,
                targetPath,
                manifestPath,
                progress,
                cancellationToken))
        {
            return artifact;
        }

        artifact.Architecture = await ReadRemoteArchitectureAsync(artifact.DownloadUrl, cancellationToken);
        EnsureRuntimeArchitectureIsEligible(artifact.Architecture);
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        SaveManifest(manifestPath, CreateManifest(artifact, "partial", existingBytes));

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("LOPATA/0.1 (+https://github.com/PiTrolKun/LOPATA)");
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existingBytes > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            existingBytes = 0;
            File.Delete(partialPath);
        }

        response.EnsureSuccessStatusCode();
        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        var buffer = new byte[1024 * 1024];
        var downloadedBytes = existingBytes;
        var lastBytes = downloadedBytes;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var read = await remote.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;
            if (stopwatch.ElapsedMilliseconds >= 400)
            {
                progress.Report(new ExecutorDownloadProgress(
                    downloadedBytes,
                    artifact.SizeBytes,
                    (downloadedBytes - lastBytes) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                    "downloading"));
                lastBytes = downloadedBytes;
                stopwatch.Restart();
                SaveManifest(manifestPath, CreateManifest(artifact, "partial", downloadedBytes));
            }
        }

        await local.FlushAsync(cancellationToken);
        local.Close();
        if (downloadedBytes != artifact.SizeBytes)
        {
            SaveManifest(manifestPath, CreateManifest(artifact, "partial", downloadedBytes));
            throw new IOException("Executor model download is incomplete.");
        }

        progress.Report(new ExecutorDownloadProgress(downloadedBytes, artifact.SizeBytes, 0, "verifying"));
        var actualHash = await ComputeSha256Async(partialPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(artifact.Sha256)
            && !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            SaveManifest(manifestPath, CreateManifest(artifact, "invalid", downloadedBytes));
            throw new InvalidDataException("Executor model SHA-256 verification failed.");
        }

        File.Move(partialPath, targetPath, overwrite: true);
        ComponentAudit.Invalidate(directory);
        artifact.Sha256 = actualHash;
        artifact.IsInstalled = false;
        artifact.InstalledPath = targetPath;
        var verified = CreateManifest(artifact, "downloaded_verified", downloadedBytes);
        verified.VerifiedAt = DateTimeOffset.Now;
        SaveManifest(manifestPath, verified);
        progress.Report(new ExecutorDownloadProgress(downloadedBytes, artifact.SizeBytes, 0, "runtime_validation"));
        return artifact;
    }

    /// <summary>Use the pinned local executor manifest only when it names the model being loaded.</summary>
    public static async Task VerifyPathForUseAsync(string path, CancellationToken token, Action<string>? log = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var manifest = ExecutorModelManifestStore.Load(Path.Combine(directory, ManifestFileName));
        if (manifest is null || !Path.GetFileName(path).Equals(manifest.File, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64) return;
        await RuntimeFileAudit.VerifyAsync("executor:" + manifest.RepoId, directory, manifest.Sha256,
            [new PinnedTreeFile(manifest.File, manifest.TotalBytes, manifest.Sha256)], token, log);
    }

    public ExecutorModelArtifact MarkRuntimeVerified(ExecutorModelArtifact artifact)
    {
        var manifestPath = GetManifestPath(artifact);
        var previousPassport = ExecutorModelManifestStore.Load(manifestPath)?.SemanticPassport;
        var manifest = CreateManifest(artifact, "installed", artifact.SizeBytes);
        manifest.VerifiedAt = DateTimeOffset.Now;
        manifest.RuntimeVerifiedAt = DateTimeOffset.Now;
        manifest.RuntimeBackend = LlamaBackendPaths.DisplayName;
        manifest.SemanticPassport = ExecutorModelManifestStore.PreparePassport(
            manifest,
            previousPassport);
        SaveManifest(manifestPath, manifest);
        artifact.IsInstalled = true;
        return artifact;
    }

    public void MarkRuntimeIncompatible(ExecutorModelArtifact artifact, Exception error)
    {
        var manifestPath = GetManifestPath(artifact);
        var previousPassport = ExecutorModelManifestStore.Load(manifestPath)?.SemanticPassport;
        var manifest = CreateManifest(artifact, "runtime_incompatible", artifact.SizeBytes);
        manifest.VerifiedAt = DateTimeOffset.Now;
        manifest.RuntimeBackend = LlamaBackendPaths.DisplayName;
        manifest.RuntimeError = error.Message;
        manifest.SemanticPassport = previousPassport ?? new ModelSemanticPassport();
        SaveManifest(manifestPath, manifest);
        artifact.IsInstalled = false;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static void SaveManifest(string path, ExecutorModelManifest manifest)
    {
        var previousPassport = ExecutorModelManifestStore.Load(path)?.SemanticPassport;
        if (manifest.SemanticPassport.Status == ModelSemanticPassportStatuses.Missing
            && previousPassport is not null
            && previousPassport.Status != ModelSemanticPassportStatuses.Missing)
        {
            manifest.SemanticPassport = previousPassport;
        }

        ExecutorModelManifestStore.Save(path, manifest);
    }

    private static ExecutorModelManifest CreateManifest(
        ExecutorModelArtifact artifact,
        string status,
        long downloadedBytes) =>
        new()
        {
            RequestedModel = artifact.RequestedModel,
            RepoId = artifact.RepoId,
            File = artifact.FileName,
            Quantization = artifact.Quantization,
            Source = artifact.DownloadUrl,
            Sha256 = artifact.Sha256,
            License = artifact.License,
            Architecture = artifact.Architecture,
            Status = status,
            DownloadedBytes = downloadedBytes,
            TotalBytes = artifact.SizeBytes
        };

    private async Task<bool> TryUseVerifiedLocalFileAsync(
        ExecutorModelArtifact artifact,
        string targetPath,
        string manifestPath,
        IProgress<ExecutorDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != artifact.SizeBytes)
        {
            return false;
        }

        var actualHash = await ComputeSha256Async(targetPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(artifact.Sha256)
            && !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        artifact.Architecture = await ReadLocalArchitectureAsync(targetPath, cancellationToken);
        EnsureRuntimeArchitectureIsEligible(artifact.Architecture);
        artifact.Sha256 = actualHash;
        artifact.InstalledPath = targetPath;
        artifact.IsInstalled = false;
        var manifest = CreateManifest(artifact, "downloaded_verified", artifact.SizeBytes);
        manifest.VerifiedAt = DateTimeOffset.Now;
        SaveManifest(manifestPath, manifest);
        progress.Report(new ExecutorDownloadProgress(artifact.SizeBytes, artifact.SizeBytes, 0, "runtime_validation"));
        return true;
    }

    private async Task<string> ReadRemoteArchitectureAsync(string source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd("LOPATA/0.1 (+https://github.com/PiTrolKun/LOPATA)");
        request.Headers.Range = new RangeHeaderValue(0, GgufHeaderProbeBytes - 1);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return GgufMetadataReader.Read(await ReadPrefixAsync(stream, cancellationToken)).Architecture;
    }

    private static async Task<string> ReadLocalArchitectureAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return GgufMetadataReader.Read(await ReadPrefixAsync(stream, cancellationToken)).Architecture;
    }

    private static async Task<byte[]> ReadPrefixAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(GgufHeaderProbeBytes);
        var block = new byte[64 * 1024];
        while (buffer.Length < GgufHeaderProbeBytes)
        {
            var remaining = GgufHeaderProbeBytes - (int)buffer.Length;
            var read = await stream.ReadAsync(block.AsMemory(0, Math.Min(block.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static void EnsureRuntimeArchitectureIsEligible(string architecture)
    {
        if (GgufMetadataReader.IsKnownUnsupportedArchitecture(architecture))
        {
            throw new InvalidDataException(
                $"GGUF architecture '{architecture}' is an auxiliary model and cannot be used as the main executor.");
        }
    }

    private static string GetManifestPath(ExecutorModelArtifact artifact)
    {
        var directory = Path.GetDirectoryName(artifact.InstalledPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Executor artifact does not have a local installation directory.");
        }

        return Path.Combine(directory, ManifestFileName);
    }

    private static string CreateSafeDirectoryName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character).ToArray();
        var safe = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "executor" : safe;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static long GetFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }
}
