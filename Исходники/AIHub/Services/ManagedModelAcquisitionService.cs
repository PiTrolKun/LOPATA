using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ManagedModelAcquisitionService : IDisposable
{
    private const long VerificationReserveBytes = 512L * 1024 * 1024;
    private readonly ManagedModelLibraryStore _store;
    private readonly HttpClient _httpClient;
    private readonly SegmentedModelFileDownloader _downloader;
    private readonly bool _ownsClient;

    public ManagedModelAcquisitionService(
        ManagedModelLibraryStore store,
        HttpClient? httpClient = null,
        long segmentedMinimumBytes = 64L * 1024 * 1024)
    {
        _store = store;
        _httpClient = httpClient ?? new HttpClient();
        _downloader = new SegmentedModelFileDownloader(_httpClient, segmentedMinimumBytes);
        _ownsClient = httpClient is null;
    }

    public int MaximumParallelConnections
    {
        get => _downloader.MaximumParallelConnections;
        set => _downloader.MaximumParallelConnections = value;
    }

    public async Task<ManagedModelArtifactCard> DownloadAsync(
        string modelArtifactId,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var card = _store.Load(modelArtifactId)
            ?? throw new InvalidOperationException("The model card is not registered in the LOPATA library.");
        await ComponentLicenseGate.EnsureAsync(modelArtifactId, cancellationToken);
        EnsureDownloadable(card);
        var installRoot = Path.GetFullPath(card.InstallDirectory);
        Directory.CreateDirectory(installRoot);
        EnsureEnoughSpace(card, installRoot, forceFullDownload: false);

        card.Status = ManagedModelStatuses.Downloading;
        card.LastError = string.Empty;
        _store.Upsert(card);
        try
        {
            long completedBytes = 0;
            foreach (var file in card.Files.Where(file => file.IsRequired))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetContainedPath(installRoot, file.RelativePath);
                if (await IsFileValidAsync(targetPath, file, cancellationToken))
                {
                    completedBytes += file.SizeBytes;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await DownloadFileAsync(card, file, targetPath, completedBytes, progress, cancellationToken);
                completedBytes += file.SizeBytes;
                card.StoredBytes = CountStoredBytes(card, installRoot);
                _store.Upsert(card);
            }

            card = await VerifyAsync(card.ModelArtifactId, progress, cancellationToken);
            if (card.Status == ManagedModelStatuses.NeedsVerification)
            {
                card.FirstInstalledAt ??= DateTimeOffset.Now;
                _store.Upsert(card);
            }
            return card;
        }
        catch (OperationCanceledException)
        {
            card.Status = ManagedModelStatuses.Paused;
            card.StoredBytes = CountStoredBytes(card, installRoot);
            _store.Upsert(card);
            throw;
        }
        catch (HttpRequestException ex)
        {
            card.Status = ManagedModelStatuses.SourceUnavailable;
            card.LastError = ex.Message;
            card.StoredBytes = CountStoredBytes(card, installRoot);
            _store.Upsert(card);
            throw;
        }
        catch (Exception ex)
        {
            card.Status = ManagedModelStatuses.Corrupted;
            card.LastError = ex.Message;
            card.StoredBytes = CountStoredBytes(card, installRoot);
            _store.Upsert(card);
            throw;
        }
    }

    public async Task<ManagedModelArtifactCard> ReinstallAsync(
        string modelArtifactId,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var card = _store.Load(modelArtifactId)
            ?? throw new InvalidOperationException("The model card is not registered in the LOPATA library.");
        await ComponentLicenseGate.EnsureAsync(modelArtifactId, cancellationToken);
        EnsureDownloadable(card);
        var installRoot = Path.GetFullPath(card.InstallDirectory);
        Directory.CreateDirectory(installRoot);
        EnsureEnoughSpace(card, installRoot, forceFullDownload: true);
        var previousStatus = card.Status;
        var previousVerifiedAt = card.LastVerifiedAt;
        var previousRuntimeVerifiedAt = card.RuntimeVerifiedAt;
        card.Status = ManagedModelStatuses.Downloading;
        card.LastError = string.Empty;
        _store.Upsert(card);
        try
        {
            long completedBytes = 0;
            foreach (var file in card.Files.Where(file => file.IsRequired))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = GetContainedPath(installRoot, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await DownloadFileAsync(card, file, targetPath, completedBytes, progress, cancellationToken);
                completedBytes += file.SizeBytes;
            }
            return await VerifyAsync(card.ModelArtifactId, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            card.Status = previousStatus == ManagedModelStatuses.Installed
                ? previousStatus
                : ManagedModelStatuses.Paused;
            card.LastVerifiedAt = previousVerifiedAt;
            card.RuntimeVerifiedAt = previousRuntimeVerifiedAt;
            card.StoredBytes = CountStoredBytes(card, installRoot);
            _store.Upsert(card);
            throw;
        }
        catch (Exception ex)
        {
            card.Status = previousStatus == ManagedModelStatuses.Installed
                ? previousStatus
                : ex is HttpRequestException
                    ? ManagedModelStatuses.SourceUnavailable
                    : ManagedModelStatuses.Corrupted;
            card.LastVerifiedAt = previousVerifiedAt;
            card.RuntimeVerifiedAt = previousRuntimeVerifiedAt;
            card.LastError = ex.Message;
            card.StoredBytes = CountStoredBytes(card, installRoot);
            _store.Upsert(card);
            throw;
        }
    }

    public async Task<ManagedModelArtifactCard> VerifyAsync(
        string modelArtifactId,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var card = _store.Load(modelArtifactId)
            ?? throw new InvalidOperationException("The model card is not registered in the LOPATA library.");
        EnsureManagedPath(card);
        var installRoot = Path.GetFullPath(card.InstallDirectory);
        var verifiedBytes = 0L;
        foreach (var file in card.Files.Where(file => file.IsRequired))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ManagedModelDownloadProgress(
                card.ModelArtifactId,
                file.RelativePath,
                verifiedBytes,
                card.TotalBytes,
                0,
                "verifying"));
            var path = GetContainedPath(installRoot, file.RelativePath);
            if (!await IsFileValidAsync(path, file, cancellationToken))
            {
                card.StoredBytes = CountStoredBytes(card, installRoot);
                card.Status = card.StoredBytes == 0
                    ? ManagedModelStatuses.FilesRemoved
                    : ManagedModelStatuses.Corrupted;
                card.LastError = $"Integrity verification failed: {file.RelativePath}";
                _store.Upsert(card);
                return card;
            }
            var verifiedInfo = new FileInfo(path);
            file.VerifiedSizeBytes = verifiedInfo.Length;
            file.VerifiedLastWriteTimeUtc = verifiedInfo.LastWriteTimeUtc;
            verifiedBytes += file.SizeBytes;
        }

        card.StoredBytes = verifiedBytes;
        card.LastVerifiedAt = DateTimeOffset.Now;
        card.LastError = string.Empty;
        card.Status = RequiresRuntimeSmokeCheck(card)
            ? ManagedModelStatuses.NeedsVerification
            : ManagedModelStatuses.Installed;
        card.FirstInstalledAt ??= DateTimeOffset.Now;
        _store.Upsert(card);
        progress?.Report(new ManagedModelDownloadProgress(
            card.ModelArtifactId,
            string.Empty,
            verifiedBytes,
            card.TotalBytes,
            0,
            "verified"));
        return card;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadFileAsync(
        ManagedModelArtifactCard card,
        ManagedModelArtifactFile file,
        string targetPath,
        long completedBytes,
        IProgress<ManagedModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureEnoughSpaceForFile(file, targetPath);
        var partialPath = targetPath + ".part";
        await _downloader.DownloadAsync(
            card,
            file,
            targetPath,
            completedBytes,
            progress,
            cancellationToken);
        if (!await IsFileValidAsync(partialPath, file, cancellationToken))
        {
            DeletePartialArtifacts(targetPath);
            throw new InvalidDataException($"SHA-256 verification failed: {file.RelativePath}");
        }
        File.Move(partialPath, targetPath, overwrite: true);
        DeletePartialArtifacts(targetPath);
    }

    private static async Task<bool> IsFileValidAsync(
        string path,
        ManagedModelArtifactFile file,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.SizeBytes || string.IsNullOrWhiteSpace(file.Sha256))
        {
            return false;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static long CountStoredBytes(ManagedModelArtifactCard card, string installRoot) => card.Files.Sum(file =>
        SegmentedModelFileDownloader.GetStoredBytes(
            GetContainedPath(installRoot, file.RelativePath),
            file.SizeBytes));

    private static void DeletePartialArtifacts(string targetPath)
    {
        foreach (var path in SegmentedModelFileDownloader.GetPartialArtifactPaths(targetPath))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private void EnsureEnoughSpaceForFile(ManagedModelArtifactFile file, string targetPath)
    {
        var storedPartialBytes = SegmentedModelFileDownloader.GetPartialStoredBytes(
            targetPath,
            file.SizeBytes);
        var missingBytes = Math.Max(0, file.SizeBytes - storedPartialBytes);
        var assemblyReserve = _downloader.GetAssemblyReserveBytes(targetPath, file.SizeBytes);
        var driveRoot = Path.GetPathRoot(targetPath);
        if (!string.IsNullOrWhiteSpace(driveRoot)
            && new DriveInfo(driveRoot).AvailableFreeSpace
                < missingBytes + assemblyReserve + VerificationReserveBytes)
        {
            throw new IOException("Not enough free space for the model download, segment assembly and verification reserve.");
        }
    }

    private static bool RequiresRuntimeSmokeCheck(ManagedModelArtifactCard card) =>
        card.ModelArtifactId is not (ManagedModelCatalog.Qwen25OmniHeavyArtifactId or ManagedModelCatalog.OmniAlphaArtifactId or ManagedModelCatalog.OmniBetaArtifactId)
        && card.Role is (ManagedModelRoles.Vision or ManagedModelRoles.Localizer);

    private static void EnsureDownloadable(ManagedModelArtifactCard card)
    {
        EnsureManagedPath(card);
        if (card.Files.Count == 0 || card.Files.Any(file => file.SizeBytes <= 0 || string.IsNullOrWhiteSpace(file.Sha256)))
        {
            throw new InvalidDataException("The exact model artifact manifest is incomplete.");
        }
    }

    private static void EnsureManagedPath(ManagedModelArtifactCard card)
    {
        if (!card.IsManaged || string.IsNullOrWhiteSpace(card.InstallDirectory) || string.IsNullOrWhiteSpace(card.ModelsRoot))
        {
            throw new InvalidOperationException("The model is external or its managed storage is not configured.");
        }
        var root = Path.GetFullPath(card.ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var install = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!install.StartsWith(root, StringComparison.OrdinalIgnoreCase) || install == root)
        {
            throw new InvalidOperationException("The managed model directory is outside the configured models storage.");
        }
    }

    private static string GetContainedPath(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The model manifest contains an unsafe file path.");
        }
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The model file path escapes the managed model directory.");
        }
        return path;
    }

    private static void EnsureEnoughSpace(
        ManagedModelArtifactCard card,
        string installRoot,
        bool forceFullDownload)
    {
        var missingBytes = forceFullDownload ? card.TotalBytes : card.Files.Sum(file =>
        {
            var target = GetContainedPath(installRoot, file.RelativePath);
            var existing = SegmentedModelFileDownloader.GetStoredBytes(target, file.SizeBytes);
            return Math.Max(0, file.SizeBytes - existing);
        });
        var driveRoot = Path.GetPathRoot(installRoot);
        if (!string.IsNullOrWhiteSpace(driveRoot)
            && new DriveInfo(driveRoot).AvailableFreeSpace < missingBytes + VerificationReserveBytes)
        {
            throw new IOException("Not enough free space for the model download and verification reserve.");
        }
    }
}
