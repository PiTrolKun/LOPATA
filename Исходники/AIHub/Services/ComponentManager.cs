using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHub.Models;
using SharpCompress.Archives;

namespace AIHub.Services;

public sealed partial class ComponentManager
{
    private const long MaximumExtractedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 100_000;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromHours(8)
    };

    private readonly ComponentStateStore _stateStore;
    private readonly ComponentEventLog _eventLog = new();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public ComponentManager(ComponentStateStore? stateStore = null)
    {
        _stateStore = stateStore ?? new ComponentStateStore();
    }

    public IReadOnlyList<ComponentStatusSnapshot> GetStatus(string? kind = null)
    {
        var state = _stateStore.Load();
        var snapshots = ComponentCatalog.All
            .Select(entry => CreateSnapshot(entry, FindRecord(state, entry)))
            .ToList();
        var byId = snapshots.ToDictionary(
            snapshot => snapshot.Entry.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            snapshot.DependenciesAvailable = AreDependenciesAvailable(
                snapshot.Entry,
                byId,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return snapshots
            .Where(snapshot => kind is null
                || string.Equals(snapshot.Entry.Kind, kind, StringComparison.Ordinal))
            .ToList();
    }

    public bool IsCapabilityAvailable(string capability) => GetAvailableCapabilities()
        .Contains(capability, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetAvailableCapabilities() => GetStatus(ComponentKinds.Processing)
        .Where(snapshot => snapshot.IsAvailable && snapshot.Entry.IsVisibleToAi)
        .SelectMany(snapshot => snapshot.Entry.Capabilities)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public ComponentAcquisitionPlan BuildPlan(
        IEnumerable<string> capabilities,
        string reason,
        bool required = true)
    {
        var requested = capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statuses = GetStatus(ComponentKinds.Processing)
            .ToDictionary(item => item.Entry.Id, StringComparer.OrdinalIgnoreCase);
        var providerIds = requested
            .Select(capability => ComponentCatalog.FindProviders(capability)
                .OrderByDescending(entry =>
                    statuses.TryGetValue(entry.Id, out var status) && status.IsAvailable)
                .ThenBy(entry => entry.IsPlanned)
                .ThenBy(entry => entry.DownloadSizeBytes)
                .FirstOrDefault()?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return BuildPlanForComponents(providerIds, reason, required);
    }

    public ComponentAcquisitionPlan BuildPlanForComponents(
        IEnumerable<string> componentIds,
        string reason,
        bool required = true)
    {
        var requestedIds = componentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedIds.Count == 0)
        {
            return new ComponentAcquisitionPlan();
        }

        var entries = ComponentCatalog.ResolveDependencies(requestedIds);
        var statuses = GetStatus(ComponentKinds.Processing)
            .ToDictionary(item => item.Entry.Id, StringComparer.OrdinalIgnoreCase);
        return new ComponentAcquisitionPlan
        {
            Items = entries.Select(entry => new ComponentAcquisitionItem
            {
                ComponentId = entry.Id,
                Name = entry.Name,
                AlreadyAvailable = statuses.TryGetValue(entry.Id, out var status) && status.IsAvailable,
                Required = required,
                DownloadSizeBytes = entry.DownloadSizeBytes,
                Reason = reason
            }).ToList()
        };
    }

    public async Task<ComponentStatusSnapshot> DownloadAndInstallAsync(
        string componentId,
        IProgress<ComponentDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool forceReinstall = false)
    {
        var ordered = ComponentCatalog.ResolveDependencies([componentId]);
        await ComponentLicenseGate.EnsureAsync(ordered.SelectMany(x =>
            x.LicenseIds.Count > 0 ? x.LicenseIds : new[] { x.Id }).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
        ComponentStatusSnapshot? result = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = ordered[index];
            result = await DownloadSingleAsync(entry, progress, cancellationToken, forceReinstall && entry.Id == componentId);
            if (!result.IsAvailable && index < ordered.Count - 1)
            {
                throw new InvalidOperationException(
                    $"Dependency '{entry.Name}' requires installation and verification before the plan can continue.");
            }
        }

        return result ?? throw new InvalidOperationException("Component plan is empty.");
    }

    public ComponentStatusSnapshot Verify(string componentId)
    {
        var entry = ComponentCatalog.Find(componentId)
            ?? throw new InvalidOperationException($"Unknown component '{componentId}'.");
        var state = _stateStore.Load();
        var record = FindRecord(state, entry);
        var snapshot = CreateSnapshot(entry, record);
        if (entry.IsBuiltIn || snapshot.IsHealthy)
        {
            record.Status = entry.IsBuiltIn
                ? ComponentInstallStatuses.BuiltIn
                : ComponentInstallStatuses.Installed;
            record.VerifiedAt = DateTimeOffset.Now;
            record.LastError = string.Empty;
        }
        else if (entry.IsPlanned)
        {
            record.Status = ComponentInstallStatuses.Planned;
        }
        else
        {
            record.Status = ComponentInstallStatuses.NeedsVerification;
            record.LastError = "Health-check did not find the expected installed artifact.";
        }

        Upsert(state, record);
        _stateStore.Save(state);
        _eventLog.Write("component_verified", new
        {
            entry.Id,
            record.Status,
            record.InstallPath,
            record.LastError
        });
        return GetStatus().First(snapshot =>
            string.Equals(snapshot.Entry.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
    }

    public void Remove(string componentId)
    {
        var entry = ComponentCatalog.Find(componentId)
            ?? throw new InvalidOperationException($"Unknown component '{componentId}'.");
        if (entry.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in components cannot be removed.");
        }

        var state = _stateStore.Load();
        var record = FindRecord(state, entry);
        if (entry.DeliveryKind != ComponentDeliveryKinds.SystemInstaller)
        {
            DeleteContainedDirectory(record.InstallPath);
        }
        DeleteContainedFile(record.DownloadPath);
        state.Components.RemoveAll(item => string.Equals(
            item.ComponentId,
            componentId,
            StringComparison.OrdinalIgnoreCase));
        _stateStore.Save(state);
        _eventLog.Write("component_removed", new { entry.Id });
    }

    public void LaunchSystemInstaller(string componentId)
    {
        var entry = ComponentCatalog.Find(componentId)
            ?? throw new InvalidOperationException($"Unknown component '{componentId}'.");
        if (entry.DeliveryKind != ComponentDeliveryKinds.SystemInstaller)
        {
            throw new InvalidOperationException("The component is not a system installer.");
        }

        var state = _stateStore.Load();
        var record = FindRecord(state, entry);
        if (string.IsNullOrWhiteSpace(record.DownloadPath)
            || !File.Exists(record.DownloadPath))
        {
            throw new FileNotFoundException("The trusted installer has not been downloaded.", record.DownloadPath);
        }

        var fullPath = Path.GetFullPath(record.DownloadPath);
        var downloadsRoot = Path.GetFullPath(AppDataPaths.ComponentDownloadsDirectory)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(downloadsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to launch an installer outside the component store.");
        }

        Process.Start(new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true
        });
        _eventLog.Write("component_system_installer_launched", new
        {
            entry.Id,
            record.DownloadPath
        });
    }

    public string GetInstallDirectory(ComponentCatalogEntry entry)
    {
        var root = entry.Kind == ComponentKinds.Viewer
            ? AppDataPaths.ComponentViewersDirectory
            : entry.Id.StartsWith("language.", StringComparison.Ordinal)
                ? AppDataPaths.ComponentLanguagesDirectory
                : entry.Id.StartsWith("model.", StringComparison.Ordinal)
                    ? AppDataPaths.ComponentModelsDirectory
                    : AppDataPaths.ComponentRuntimesDirectory;
        return Path.Combine(root, SanitizePathSegment(entry.Id), SanitizePathSegment(entry.Version));
    }

    private async Task<ComponentStatusSnapshot> DownloadSingleAsync(
        ComponentCatalogEntry entry,
        IProgress<ComponentDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool forceReinstall = false)
    {
        if (entry.IsBuiltIn || entry.IsPlanned)
        {
            return Verify(entry.Id);
        }

        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            var current = GetStatus().First(status => status.Entry.Id == entry.Id);
            if (current.IsAvailable && !forceReinstall)
            {
                return current;
            }
            if (entry.DeliveryKind == ComponentDeliveryKinds.SystemInstaller
                && !string.IsNullOrWhiteSpace(current.Record.DownloadPath)
                && File.Exists(current.Record.DownloadPath))
            {
                return current;
            }

            AppDataPaths.EnsureComponentDirectories();
            EnsureLocalCompatibility(entry);
            if (entry.DeliveryKind == ComponentDeliveryKinds.PythonProfile)
                return await InstallPythonProfileAsync(entry, progress, cancellationToken);
            var state = _stateStore.Load();
            var record = FindRecord(state, entry);
            var downloadPath = Path.Combine(AppDataPaths.ComponentDownloadsDirectory, entry.FileName);
            var partialPath = downloadPath + ".part";
            record.Status = ComponentInstallStatuses.Downloading;
            record.DownloadPath = downloadPath;
            record.TotalBytes = entry.DownloadSizeBytes;
            Upsert(state, record);
            _stateStore.Save(state);
            _eventLog.Write("component_download_started", new
            {
                entry.Id,
                entry.Version,
                entry.Source,
                entry.DownloadUrl,
                entry.DownloadSizeBytes
            });

            var verifiedCache = !string.IsNullOrWhiteSpace(entry.Sha256) && File.Exists(downloadPath)
                && new FileInfo(downloadPath).Length == entry.DownloadSizeBytes
                && string.Equals(await ComputeSha256Async(downloadPath, cancellationToken), entry.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!verifiedCache)
            {
                await DownloadWithResumeAsync(entry, partialPath, progress, cancellationToken);
                File.Move(partialPath, downloadPath, true);
            }
            var actualSize = new FileInfo(downloadPath).Length;
            if (entry.DownloadSizeBytes > 0
                && Math.Abs(actualSize - entry.DownloadSizeBytes) > Math.Max(1024 * 1024, entry.DownloadSizeBytes / 50))
            {
                throw new InvalidDataException(
                    $"Downloaded size {actualSize} differs from catalog size {entry.DownloadSizeBytes}.");
            }

            var hash = await ComputeSha256Async(downloadPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(entry.Sha256)
                && !string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded component SHA-256 does not match the trusted catalog.");
            }

            record.DownloadedBytes = actualSize;
            record.TotalBytes = actualSize;
            record.ComputedSha256 = hash;
            record.DownloadedAt = DateTimeOffset.Now;
            record.Status = ComponentInstallStatuses.Downloaded;
            record.LastError = string.Empty;
            Upsert(state, record);
            _stateStore.Save(state);
            _eventLog.Write("component_download_completed", new
            {
                entry.Id,
                Bytes = actualSize,
                Sha256 = hash
            });

            if (entry.DeliveryKind == ComponentDeliveryKinds.SystemInstaller)
            {
                record.Status = ComponentInstallStatuses.NeedsVerification;
                record.LastError = "Downloaded. System installation requires a separate explicit user action.";
                Upsert(state, record);
                _stateStore.Save(state);
                return CreateSnapshot(entry, record);
            }

            var installDirectory = GetInstallDirectory(entry);
            var temporaryDirectory = installDirectory + ".installing";
            DeleteContainedDirectory(temporaryDirectory);
            Directory.CreateDirectory(temporaryDirectory);
            if (entry.DeliveryKind == ComponentDeliveryKinds.File)
            {
                File.Copy(downloadPath, Path.Combine(temporaryDirectory, entry.FileName), true);
            }
            else
            {
                ExtractArchiveSafely(downloadPath, temporaryDirectory);
            }

            // Validate staged libraries while the previous installation is still intact.
            if (HardwareRuntimeBundleVerifier.IsManaged(entry.Id))
                await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(entry.Id, temporaryDirectory, cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);
            var previousDirectory = installDirectory + ".previous";
            DeleteContainedDirectory(previousDirectory);
            if (Directory.Exists(installDirectory))
            {
                Directory.Move(installDirectory, previousDirectory);
            }

            try
            {
                Directory.Move(temporaryDirectory, installDirectory);
                record.InstallPath = installDirectory;
                record.InstalledAt = DateTimeOffset.Now;
                record.Status = ComponentInstallStatuses.Installed;
                record.LastError = string.Empty;
                Upsert(state, record);
                _stateStore.Save(state);
                var verified = Verify(entry.Id);
                if (!verified.IsAvailable)
                {
                    throw new InvalidDataException(
                        "Installed component did not pass its health-check.");
                }
                DeleteContainedDirectory(previousDirectory);
                return verified;
            }
            catch
            {
                DeleteContainedDirectory(installDirectory);
                if (Directory.Exists(previousDirectory))
                {
                    Directory.Move(previousDirectory, installDirectory);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            var state = _stateStore.Load();
            var record = FindRecord(state, entry);
            record.Status = ComponentInstallStatuses.Failed;
            record.LastError = ex.Message;
            Upsert(state, record);
            _stateStore.Save(state);
            _eventLog.Write("component_install_failed", new
            {
                entry.Id,
                ex.Message,
                ErrorType = ex.GetType().FullName
            });
            throw;
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static async Task DownloadWithResumeAsync(
        ComponentCatalogEntry entry,
        string partialPath,
        IProgress<ComponentDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUrl);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }
        response.EnsureSuccessStatusCode();

        var responseLength = response.Content.Headers.ContentLength ?? 0;
        var totalLength = existingLength + responseLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        var downloaded = existingLength;
        var started = Stopwatch.StartNew();
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            var rate = downloaded / Math.Max(0.1, started.Elapsed.TotalSeconds);
            progress?.Report(new ComponentDownloadProgress(
                entry.Id,
                downloaded,
                totalLength,
                rate,
                ComponentInstallStatuses.Downloading));
        }
    }

    private static void ExtractArchiveSafely(string archivePath, string destination)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long extractedBytes = 0;
        var entryCount = 0;
        foreach (var entry in archive.Entries)
        {
            entryCount++;
            if (entryCount > MaximumArchiveEntries)
            {
                throw new InvalidDataException("Archive contains too many entries.");
            }

            var entryKey = string.IsNullOrWhiteSpace(entry.Key)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : entry.Key;
            var targetPath = Path.GetFullPath(Path.Combine(destination, entryKey));
            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Archive entry escapes the target directory.");
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var input = entry.OpenEntryStream();
            using var output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                extractedBytes += read;
                if (extractedBytes > MaximumExtractedBytes)
                {
                    throw new InvalidDataException("Archive exceeds the extraction size limit.");
                }
                output.Write(buffer, 0, read);
            }
        }
    }

    private ComponentStatusSnapshot CreateSnapshot(
        ComponentCatalogEntry entry,
        ComponentInstallationRecord record)
    {
        if (entry.IsBuiltIn)
        {
            record.Status = ComponentInstallStatuses.BuiltIn;
            return new ComponentStatusSnapshot
            {
                Entry = entry,
                Record = record,
                IsInstalled = true,
                IsHealthy = true
            };
        }

        if (entry.IsPlanned)
        {
            record.Status = ComponentInstallStatuses.Planned;
            return new ComponentStatusSnapshot
            {
                Entry = entry,
                Record = record,
                IsInstalled = false,
                IsHealthy = false
            };
        }

        var installPath = record.InstallPath;
        if (entry.DeliveryKind == ComponentDeliveryKinds.SystemInstaller)
        {
            var discoveredPath = FindSystemInstallation(entry.Id);
            if (!string.IsNullOrWhiteSpace(discoveredPath))
            {
                installPath = discoveredPath;
                record.InstallPath = discoveredPath;
            }
        }

        var installed = !string.IsNullOrWhiteSpace(installPath)
            && (Directory.Exists(installPath) || File.Exists(installPath));
        var healthy = installed && (entry.DeliveryKind == ComponentDeliveryKinds.SystemInstaller
            || string.IsNullOrWhiteSpace(entry.HealthCheckRelativePath)
            || FindExpectedArtifact(installPath, entry.HealthCheckRelativePath));
        if (healthy && HardwareRuntimeBundleVerifier.IsManaged(entry.Id))
            healthy = HardwareRuntimeBundleVerifier.HasCompleteLayout(entry.Id, installPath);
        if (healthy && entry.DeliveryKind == ComponentDeliveryKinds.PythonProfile)
            healthy = record.Version == entry.Version && record.VerifiedAt is not null
                && record.ComputedSha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)
                && ManagedModelPathIdentity.SameDirectory(installPath, GetInstallDirectory(entry))
                && PythonRuntimeBundleVerifier.HasCpuLayout(installPath);
        return new ComponentStatusSnapshot
        {
            Entry = entry,
            Record = record,
            IsInstalled = installed,
            IsHealthy = healthy
        };
    }

    private static bool FindExpectedArtifact(string root, string relativePath)
    {
        if (File.Exists(root))
        {
            return string.Equals(
                Path.GetFileName(root),
                Path.GetFileName(relativePath),
                StringComparison.OrdinalIgnoreCase);
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(root, normalized)))
        {
            return true;
        }

        var fileName = Path.GetFileName(normalized);
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).Any();
    }

    private static bool AreDependenciesAvailable(
        ComponentCatalogEntry entry,
        IReadOnlyDictionary<string, ComponentStatusSnapshot> snapshots,
        ISet<string> visiting)
    {
        if (!visiting.Add(entry.Id))
        {
            return false;
        }

        try
        {
            foreach (var dependencyId in entry.Dependencies)
            {
                if (!snapshots.TryGetValue(dependencyId, out var dependency)
                    || !dependency.IsSelfAvailable
                    || !AreDependenciesAvailable(dependency.Entry, snapshots, visiting))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            visiting.Remove(entry.Id);
        }
    }

    private static string FindSystemInstallation(string componentId)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return componentId switch
        {
            "runtime.tesseract" => FirstExistingFile(
                Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe")),
            "runtime.libreoffice" => FirstExistingFile(
                Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe")),
            "viewer.webview2" => FindWebView2Executable(programFilesX86),
            _ => string.Empty
        };
    }

    private static string FindWebView2Executable(string programFilesX86)
    {
        var applicationRoot = Path.Combine(
            programFilesX86,
            "Microsoft",
            "EdgeWebView",
            "Application");
        if (!Directory.Exists(applicationRoot))
        {
            return string.Empty;
        }

        return Directory.EnumerateFiles(
                applicationRoot,
                "msedgewebview2.exe",
                SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? string.Empty;
    }

    private static string FirstExistingFile(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists) ?? string.Empty;

    private static void EnsureLocalCompatibility(ComponentCatalogEntry entry)
    {
        if (string.Equals(entry.Architecture, "x64", StringComparison.OrdinalIgnoreCase)
            && !Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException(
                $"Component '{entry.Name}' requires 64-bit Windows.");
        }

        var root = Path.GetPathRoot(Path.GetFullPath(AppDataPaths.ComponentsDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var requiredBytes = Math.Max(
            entry.DownloadSizeBytes + entry.InstalledSizeBytes,
            entry.DownloadSizeBytes * 2);
        requiredBytes += 256L * 1024 * 1024;
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"Not enough free space for '{entry.Name}'. Required: {requiredBytes} bytes; available: {drive.AvailableFreeSpace} bytes.");
        }
    }

    private static ComponentInstallationRecord FindRecord(
        ComponentStateDocument state,
        ComponentCatalogEntry entry) => state.Components.FirstOrDefault(item =>
            string.Equals(item.ComponentId, entry.Id, StringComparison.OrdinalIgnoreCase))
        ?? new ComponentInstallationRecord
        {
            ComponentId = entry.Id,
            Version = entry.Version,
            Status = entry.IsBuiltIn
                ? ComponentInstallStatuses.BuiltIn
                : entry.IsPlanned
                    ? ComponentInstallStatuses.Planned
                    : ComponentInstallStatuses.NotInstalled
        };

    private static void Upsert(ComponentStateDocument state, ComponentInstallationRecord record)
    {
        state.Components.RemoveAll(item => string.Equals(
            item.ComponentId,
            record.ComponentId,
            StringComparison.OrdinalIgnoreCase));
        state.Components.Add(record);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static void DeleteContainedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(AppDataPaths.ComponentsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the component store.");
        }

        Directory.Delete(fullPath, true);
    }

    private static void DeleteContainedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(AppDataPaths.ComponentsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a file outside the component store.");
        }

        File.Delete(fullPath);
    }
}
