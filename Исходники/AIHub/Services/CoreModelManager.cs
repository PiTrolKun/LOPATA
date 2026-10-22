using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class CoreModelManager
{
    public const string CoreModelDisplayName = "Qwen3 8B Q4_K_M";
    public const string CoreModelFileName = "Qwen3-8B-Q4_K_M.gguf";
    public const long CoreModelTotalBytes = 5_027_783_488;
    public const long RecommendedFreeBytes = 10L * 1024 * 1024 * 1024;

    private const string CoreModelFolderName = "Qwen3-8B";
    private const string ManifestFileName = "core-model.json";
    private const string PartialExtension = ".part";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient = new();

    public CoreModelCheckResult Check(StorageSettings storageSettings)
    {
        var modelsRoot = GetModelsRoot(storageSettings);
        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            return new CoreModelCheckResult
            {
                Availability = CoreModelAvailability.StorageNotConfigured,
                RequiredBytes = CoreModelTotalBytes
            };
        }

        var paths = BuildPaths(modelsRoot);
        var freeBytes = GetFreeBytes(modelsRoot);
        var result = new CoreModelCheckResult
        {
            ModelsRoot = modelsRoot,
            ModelDirectory = paths.ModelDirectory,
            ModelPath = paths.ModelPath,
            PartialPath = paths.PartialPath,
            ManifestPath = paths.ManifestPath,
            RequiredBytes = CoreModelTotalBytes,
            FreeBytes = freeBytes,
            HasEnoughSpace = freeBytes < 0 || freeBytes >= RecommendedFreeBytes
        };

        if (!Directory.Exists(modelsRoot))
        {
            result.Availability = CoreModelAvailability.ModelsFolderUnavailable;
            return result;
        }

        var modelInfo = new FileInfo(paths.ModelPath);
        var partialInfo = new FileInfo(paths.PartialPath);
        if (partialInfo.Exists)
        {
            result.Availability = CoreModelAvailability.Partial;
            result.ExistingBytes = partialInfo.Length;
            SaveManifest(paths.ManifestPath, CreateManifest("partial", partialInfo.Length));
            return result;
        }

        if (!modelInfo.Exists)
        {
            result.Availability = CoreModelAvailability.Missing;
            SaveManifest(paths.ManifestPath, CreateManifest("missing", 0));
            return result;
        }

        if (!ComponentAudit.FullRequired)
        {
            result.Availability = CoreModelAvailability.Installed;
            return result;
        }

        result.ExistingBytes = modelInfo.Length;
        if (modelInfo.Length != CoreModelTotalBytes)
        {
            result.Availability = modelInfo.Length < CoreModelTotalBytes
                ? CoreModelAvailability.Partial
                : CoreModelAvailability.Invalid;
            SaveManifest(paths.ManifestPath, CreateManifest(result.Availability == CoreModelAvailability.Partial ? "partial" : "invalid", modelInfo.Length));
            return result;
        }

        var manifest = LoadManifest(paths.ManifestPath);
        if (manifest?.Status == "installed" && manifest.VerifiedAt is not null)
        {
            result.Availability = CoreModelAvailability.Installed;
            return result;
        }

        result.Availability = CoreModelAvailability.Invalid;
        SaveManifest(paths.ManifestPath, CreateManifest("invalid", modelInfo.Length));
        return result;
    }

    public async Task<CoreModelCheckResult> VerifyInstalledAsync(StorageSettings storageSettings, CancellationToken cancellationToken)
    {
        var result = Check(storageSettings);
        if (result.ModelPath is null || !File.Exists(result.ModelPath))
        {
            return result;
        }

        var fullRan = false;
        var isValid = true;
        try
        {
            await ComponentAudit.RunAsync(ManagedModelCatalog.CoreArtifactId, Path.GetDirectoryName(result.ModelPath)!, AuditRevision(),
                _ => File.Exists(result.ModelPath) ? Task.CompletedTask : Task.FromException(new FileNotFoundException("Core model is missing.")),
                async ct => {
                    fullRan = true;
                    var hash = await ComputeSha256Async(result.ModelPath, ct);
                    if (!string.Equals(hash, CreateManifest().Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Core model hash verification failed.");
                }, cancellationToken);
        }
        catch (InvalidDataException) { isValid = false; }
        result.Availability = isValid ? CoreModelAvailability.Installed : CoreModelAvailability.Invalid;

        if (fullRan && result.ManifestPath is not null)
        {
            var manifest = CreateManifest(isValid ? "installed" : "invalid", new FileInfo(result.ModelPath).Length);
            manifest.VerifiedAt = isValid ? DateTimeOffset.Now : null;
            SaveManifest(result.ManifestPath, manifest);
        }

        return result;
    }

    private static string AuditRevision() => RuntimeFileAudit.Revision(CreateManifest().SourceCommit,
        [new PinnedTreeFile(CoreModelFileName, CoreModelTotalBytes, CreateManifest().Sha256)]);

    /// <summary>Audit the configured core before its first real load; unrelated/debug models keep their own contract.</summary>
    public static async Task VerifyPathForUseAsync(string path, CancellationToken token, Action<string>? log = null)
    {
        if (!Path.GetFileName(path).Equals(CoreModelFileName, StringComparison.OrdinalIgnoreCase)) return;
        var settings = new StorageSettingsStore().LoadOrCreate();
        var modelsRoot = settings.Models.Locations.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(modelsRoot)) return;
        var expected = BuildPaths(modelsRoot).ModelPath;
        if (!Path.GetFullPath(path).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return;
        await ComponentAudit.RunAsync(ManagedModelCatalog.CoreArtifactId, Path.GetDirectoryName(expected)!, AuditRevision(),
            _ => File.Exists(expected) ? Task.CompletedTask : Task.FromException(new FileNotFoundException("Core model is missing.")),
            async ct => {
                var hash = await ComputeSha256Async(expected, ct);
                if (!hash.Equals(CreateManifest().Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Core model hash verification failed.");
            }, token, log);
    }

    public async Task DownloadAsync(
        StorageSettings storageSettings,
        IProgress<CoreModelDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var auditInstallation = ComponentAudit.Installation();
        await ComponentLicenseGate.EnsureAsync(ManagedModelCatalog.CoreArtifactId, cancellationToken);
        var modelsRoot = GetModelsRoot(storageSettings);
        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            throw new InvalidOperationException("Models storage is not configured.");
        }

        var paths = BuildPaths(modelsRoot);
        Directory.CreateDirectory(paths.ModelDirectory);

        var freeBytes = GetFreeBytes(modelsRoot);
        if (freeBytes >= 0 && freeBytes < RecommendedFreeBytes)
        {
            throw new IOException("Not enough free space for core model download.");
        }

        var existingBytes = File.Exists(paths.PartialPath) ? new FileInfo(paths.PartialPath).Length : 0;
        if (File.Exists(paths.ModelPath) && !File.Exists(paths.PartialPath))
        {
            File.Move(paths.ModelPath, paths.PartialPath, overwrite: true);
            existingBytes = new FileInfo(paths.PartialPath).Length;
        }

        SaveManifest(paths.ManifestPath, CreateManifest("partial", existingBytes));

        using var request = new HttpRequestMessage(HttpMethod.Get, CreateManifest().Source);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (existingBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existingBytes = 0;
            File.Delete(paths.PartialPath);
        }

        response.EnsureSuccessStatusCode();

        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(paths.PartialPath, FileMode.Append, FileAccess.Write, FileShare.Read);

        var buffer = new byte[1024 * 1024];
        var downloadedBytes = existingBytes;
        var lastReportBytes = downloadedBytes;
        var lastReport = Stopwatch.StartNew();
        var totalBytes = response.Content.Headers.ContentLength.HasValue
            ? existingBytes + response.Content.Headers.ContentLength.Value
            : CoreModelTotalBytes;

        while (true)
        {
            var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;

            if (lastReport.ElapsedMilliseconds >= 500)
            {
                var bytesSinceLastReport = downloadedBytes - lastReportBytes;
                progress.Report(new CoreModelDownloadProgress
                {
                    DownloadedBytes = downloadedBytes,
                    TotalBytes = totalBytes,
                    BytesPerSecond = bytesSinceLastReport / Math.Max(0.001, lastReport.Elapsed.TotalSeconds),
                    Stage = "downloading"
                });
                lastReportBytes = downloadedBytes;
                lastReport.Restart();
                SaveManifest(paths.ManifestPath, CreateManifest("partial", downloadedBytes));
            }
        }

        progress.Report(new CoreModelDownloadProgress
        {
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes,
            Stage = "verifying"
        });

        if (downloadedBytes != CoreModelTotalBytes)
        {
            SaveManifest(paths.ManifestPath, CreateManifest("partial", downloadedBytes));
            throw new IOException("Core model download is incomplete.");
        }

        local.Close();
        File.Move(paths.PartialPath, paths.ModelPath, overwrite: true);
        ComponentAudit.Invalidate(paths.ModelDirectory);

        var verified = await VerifyInstalledAsync(storageSettings, cancellationToken);
        if (verified.Availability != CoreModelAvailability.Installed)
        {
            throw new InvalidDataException("Core model hash verification failed.");
        }

        progress.Report(new CoreModelDownloadProgress
        {
            DownloadedBytes = CoreModelTotalBytes,
            TotalBytes = CoreModelTotalBytes,
            Stage = "installed"
        });
    }

    public string? GetModelsRoot(StorageSettings storageSettings)
    {
        return storageSettings.Models.Locations.FirstOrDefault()?.Path;
    }

    private static CoreModelPaths BuildPaths(string modelsRoot)
    {
        var modelDirectory = Path.Combine(modelsRoot, "Core", CoreModelFolderName);
        return new CoreModelPaths(
            modelDirectory,
            Path.Combine(modelDirectory, CoreModelFileName),
            Path.Combine(modelDirectory, CoreModelFileName + PartialExtension),
            Path.Combine(modelDirectory, ManifestFileName));
    }

    private static CoreModelManifest CreateManifest(string status = "missing", long downloadedBytes = 0)
    {
        return new CoreModelManifest
        {
            Status = status,
            DownloadedBytes = downloadedBytes,
            TotalBytes = CoreModelTotalBytes
        };
    }

    private static CoreModelManifest? LoadManifest(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<CoreModelManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveManifest(string manifestPath, CoreModelManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json);
    }

    private static long GetFreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return -1;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record CoreModelPaths(
        string ModelDirectory,
        string ModelPath,
        string PartialPath,
        string ManifestPath);
}
