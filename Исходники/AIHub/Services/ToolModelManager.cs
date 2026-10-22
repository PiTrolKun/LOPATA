using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ToolModelManager
{
    public const string RerankerDisplayName = "BAAI bge-reranker-v2-m3";
    public const long RerankerTotalBytes = 2_293_242_108;
    public const long RecommendedFreeBytes = 4L * 1024 * 1024 * 1024;

    private const string RerankerFolderName = "BAAI-bge-reranker-v2-m3";
    private const string ManifestFileName = "tool-model.json";
    private const string PartialExtension = ".part";
    private const string SourceCommit = "953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient = new();

    public bool IsRerankerInstalled(StorageSettings storageSettings)
    {
        var paths = BuildRerankerPaths(storageSettings);
        if (paths is null || !File.Exists(paths.ManifestPath))
        {
            return false;
        }

        var manifest = LoadManifest(paths.ManifestPath);
        return manifest?.Status == "installed"
            && manifest.VerifiedAt is not null
            && manifest.Files.All(file => File.Exists(Path.Combine(paths.ModelDirectory, file.File)));
    }

    public string? GetRerankerDirectory(StorageSettings storageSettings)
    {
        var paths = BuildRerankerPaths(storageSettings);
        return paths is not null && IsRerankerInstalled(storageSettings)
            ? paths.ModelDirectory
            : null;
    }

    public async Task EnsureRerankerDownloadedAsync(
        StorageSettings storageSettings,
        IProgress<CoreModelDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var auditInstallation = ComponentAudit.Installation();
        await ComponentLicenseGate.EnsureAsync("bge-reranker-v2-m3-tool", cancellationToken);
        var paths = BuildRerankerPaths(storageSettings)
            ?? throw new InvalidOperationException("Models storage is not configured.");

        if (IsRerankerInstalled(storageSettings))
        {
            return;
        }

        Directory.CreateDirectory(paths.ModelDirectory);
        var freeBytes = GetFreeBytes(paths.ModelsRoot);
        if (freeBytes >= 0 && freeBytes < RecommendedFreeBytes)
        {
            throw new IOException("Not enough free space for reranker model download.");
        }

        var manifest = CreateRerankerManifest("partial", GetExistingBytes(paths.ModelDirectory));
        SaveManifest(paths.ManifestPath, manifest);

        long completedBytes = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(paths.ModelDirectory, file.File);
            var partialPath = targetPath + PartialExtension;

            if (File.Exists(targetPath) && await IsFileValidAsync(targetPath, file, cancellationToken))
            {
                completedBytes += file.SizeBytes;
                continue;
            }

            if (File.Exists(targetPath) && !File.Exists(partialPath))
            {
                File.Move(targetPath, partialPath, overwrite: true);
            }

            completedBytes += await DownloadFileAsync(file, partialPath, targetPath, completedBytes, progress, cancellationToken);
            SaveManifest(paths.ManifestPath, CreateRerankerManifest("partial", completedBytes));
        }

        progress.Report(new CoreModelDownloadProgress
        {
            DownloadedBytes = RerankerTotalBytes,
            TotalBytes = RerankerTotalBytes,
            Stage = "verifying-reranker"
        });

        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(paths.ModelDirectory, file.File);
            if (!await IsFileValidAsync(path, file, cancellationToken))
            {
                SaveManifest(paths.ManifestPath, CreateRerankerManifest("invalid", GetExistingBytes(paths.ModelDirectory)));
                throw new InvalidDataException($"Reranker file verification failed: {file.File}");
            }
        }

        var installedManifest = CreateRerankerManifest("installed", RerankerTotalBytes);
        installedManifest.VerifiedAt = DateTimeOffset.Now;
        SaveManifest(paths.ManifestPath, installedManifest);
        progress.Report(new CoreModelDownloadProgress
        {
            DownloadedBytes = RerankerTotalBytes,
            TotalBytes = RerankerTotalBytes,
            Stage = "installed-reranker"
        });
    }

    private async Task<long> DownloadFileAsync(
        ToolModelFileManifest file,
        string partialPath,
        string targetPath,
        long completedBeforeFile,
        IProgress<CoreModelDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Source);
        request.Headers.UserAgent.ParseAdd("LOPATA/0.1 (+https://github.com/PiTrolKun/LOPATA)");
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existingBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existingBytes = 0;
            File.Delete(partialPath);
        }

        response.EnsureSuccessStatusCode();

        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.Read);

        var buffer = new byte[1024 * 1024];
        var downloadedForFile = existingBytes;
        var lastReportBytes = downloadedForFile;
        var lastReport = Stopwatch.StartNew();

        while (true)
        {
            var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedForFile += read;

            if (lastReport.ElapsedMilliseconds >= 500)
            {
                var bytesSinceLastReport = downloadedForFile - lastReportBytes;
                progress.Report(new CoreModelDownloadProgress
                {
                    DownloadedBytes = completedBeforeFile + downloadedForFile,
                    TotalBytes = RerankerTotalBytes,
                    BytesPerSecond = bytesSinceLastReport / Math.Max(0.001, lastReport.Elapsed.TotalSeconds),
                    Stage = "downloading-reranker"
                });
                lastReportBytes = downloadedForFile;
                lastReport.Restart();
            }
        }

        local.Close();
        if (downloadedForFile != file.SizeBytes)
        {
            throw new IOException($"Reranker file download is incomplete: {file.File}");
        }

        File.Move(partialPath, targetPath, overwrite: true);
        ComponentAudit.Invalidate(Path.GetDirectoryName(targetPath)!);
        return file.SizeBytes;
    }

    public Task VerifyRerankerForUseAsync(StorageSettings storageSettings, CancellationToken token)
    {
        var paths = BuildRerankerPaths(storageSettings)
            ?? throw new InvalidOperationException("Models storage is not configured.");
        return RuntimeFileAudit.VerifyAsync("bge-reranker-v2-m3-tool", paths.ModelDirectory, SourceCommit,
            CreateRerankerFiles().Select(f => new PinnedTreeFile(f.File, f.SizeBytes, f.Sha256)).ToArray(), token);
    }

    private static ToolModelManifest CreateRerankerManifest(string status = "missing", long downloadedBytes = 0)
    {
        var files = CreateRerankerFiles();
        return new ToolModelManifest
        {
            Id = "bge-reranker-v2-m3-tool",
            Name = RerankerDisplayName,
            Role = "tool",
            ToolKind = "reranker",
            Format = "safetensors",
            SourceRepository = "BAAI/bge-reranker-v2-m3",
            SourceCommit = SourceCommit,
            License = "apache-2.0",
            Status = status,
            DownloadedBytes = downloadedBytes,
            TotalBytes = files.Sum(file => file.SizeBytes),
            Files = files
        };
    }

    private static List<ToolModelFileManifest> CreateRerankerFiles()
    {
        return
        [
            CreateFile("config.json", 795, string.Empty),
            CreateFile("model.safetensors", 2_271_071_852, "d9e3e081faff1eefb84019509b2f5558fd74c1a05a2c7db22f74174fcedb5286"),
            CreateFile("sentencepiece.bpe.model", 5_069_051, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
            CreateFile("special_tokens_map.json", 964, string.Empty),
            CreateFile("tokenizer.json", 17_098_273, "69564b696052886ed0ac63fa393e928384e0f8caada38c1f4864a9bfbf379c15"),
            CreateFile("tokenizer_config.json", 1_173, string.Empty)
        ];
    }

    private static ToolModelFileManifest CreateFile(string fileName, long sizeBytes, string sha256)
    {
        return new ToolModelFileManifest
        {
            File = fileName,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            Source = $"https://huggingface.co/BAAI/bge-reranker-v2-m3/resolve/{SourceCommit}/{fileName}"
        };
    }

    private static ToolModelPaths? BuildRerankerPaths(StorageSettings storageSettings)
    {
        var modelsRoot = storageSettings.Models.Locations.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(modelsRoot))
        {
            return null;
        }

        var directory = Path.Combine(modelsRoot, "Tools", "Reranker", RerankerFolderName);
        return new ToolModelPaths(modelsRoot, directory, Path.Combine(directory, ManifestFileName));
    }

    private static async Task<bool> IsFileValidAsync(
        string path,
        ToolModelFileManifest file,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.Sha256))
        {
            return true;
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolModelManifest? LoadManifest(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<ToolModelManifest>(File.ReadAllText(manifestPath), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveManifest(string manifestPath, ToolModelManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static long GetExistingBytes(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
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

    private sealed record ToolModelPaths(
        string ModelsRoot,
        string ModelDirectory,
        string ManifestPath);
}
