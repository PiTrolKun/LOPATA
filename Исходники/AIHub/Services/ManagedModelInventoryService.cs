using System.Text.Json;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ManagedModelInventoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ManagedModelLibraryStore _store;
    private readonly ComponentManager _componentManager;

    public ManagedModelInventoryService(
        ManagedModelLibraryStore store,
        ComponentManager? componentManager = null)
    {
        _store = store;
        _componentManager = componentManager ?? new ComponentManager();
    }

    public IReadOnlyList<ManagedModelArtifactCard> Synchronize(StorageSettings settings)
    {
        foreach (var predefined in ManagedModelCatalog.CreatePredefined(settings))
        {
            _store.Addresses.Resolve(predefined);
            ApplyFastFileState(predefined, _store.Load(predefined.ModelArtifactId));
            _store.Upsert(predefined);
        }

        foreach (var root in GetModelRoots(settings))
        {
            ImportManifests(root);
        }
        ImportComponentModels();
        ImportExternalGguf(settings);
        return _store.LoadAll();
    }

    private void ImportManifests(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var path in SafeEnumerate(root, "executor-model.json"))
        {
            TryImportExecutor(path, root);
        }
        foreach (var path in SafeEnumerate(root, "tool-model.json"))
        {
            TryImportTool(path, root);
        }
    }

    private void TryImportExecutor(string manifestPath, string modelsRoot)
    {
        try
        {
            var manifest = ExecutorModelManifestStore.Load(manifestPath);
            var directory = Path.GetDirectoryName(manifestPath);
            if (manifest is null || string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(manifest.File))
            {
                return;
            }
            var (repository, revision) = ParseHuggingFaceSource(manifest.Source, manifest.RepoId);
            var card = new ManagedModelArtifactCard
            {
                Family = string.IsNullOrWhiteSpace(manifest.RequestedModel) ? manifest.RepoId : manifest.RequestedModel,
                DisplayName = string.IsNullOrWhiteSpace(manifest.RepoId) ? manifest.RequestedModel : manifest.RepoId,
                Role = ManagedModelRoles.Executor,
                Provider = "Hugging Face",
                RepositoryId = repository,
                Revision = revision,
                Format = manifest.Format,
                Architecture = manifest.Architecture,
                Quantization = manifest.Quantization,
                License = manifest.License,
                SourcePage = string.IsNullOrWhiteSpace(repository) ? string.Empty : $"https://huggingface.co/{repository}",
                IsManaged = true,
                CanRemoveFiles = true,
                SupportsDirectDownload = !string.IsNullOrWhiteSpace(manifest.Sha256)
                    && IsImmutableRevision(revision),
                InstallDirectory = directory,
                ModelsRoot = modelsRoot,
                Status = MapStatus(manifest.Status),
                StoredBytes = manifest.DownloadedBytes,
                LastVerifiedAt = manifest.VerifiedAt,
                RuntimeVerifiedAt = manifest.RuntimeVerifiedAt,
                RuntimeBackend = manifest.RuntimeBackend,
                LastError = manifest.RuntimeError,
                Origin = ManagedModelOrigins.ExistingManifest,
                SemanticPassport = manifest.SemanticPassport,
                Consumers =
                [
                    Consumer("sandbox-executor", "Песочница", "scenario")
                ],
                Files =
                [
                    new ManagedModelArtifactFile
                    {
                        RelativePath = manifest.File,
                        SourceUrl = manifest.Source,
                        SizeBytes = manifest.TotalBytes,
                        Sha256 = manifest.Sha256,
                        Purpose = "executor_model"
                    }
                ]
            };
            card.ModelArtifactId = ManagedModelLibraryStore.CreateStableId(card);
            _store.Upsert(card);
        }
        catch
        {
            // Import of one legacy manifest must not block the remaining library.
        }
    }

    private void TryImportTool(string manifestPath, string modelsRoot)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ToolModelManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            var directory = Path.GetDirectoryName(manifestPath);
            if (manifest is null || string.IsNullOrWhiteSpace(directory) || manifest.Files.Count == 0)
            {
                return;
            }
            var role = string.Equals(manifest.ToolKind, "reranker", StringComparison.OrdinalIgnoreCase)
                ? ManagedModelRoles.Reranker
                : ManagedModelRoles.Tool;
            var card = new ManagedModelArtifactCard
            {
                ModelArtifactId = string.IsNullOrWhiteSpace(manifest.Id) ? string.Empty : "legacy-" + SanitizeId(manifest.Id),
                Family = manifest.Name,
                DisplayName = manifest.Name,
                Role = role,
                Provider = "Hugging Face",
                RepositoryId = manifest.SourceRepository,
                Revision = manifest.SourceCommit,
                Format = manifest.Format,
                License = manifest.License,
                SourcePage = string.IsNullOrWhiteSpace(manifest.SourceRepository) ? string.Empty : $"https://huggingface.co/{manifest.SourceRepository}",
                IsManaged = true,
                CanRemoveFiles = true,
                SupportsDirectDownload = IsImmutableRevision(manifest.SourceCommit)
                    && manifest.Files.All(file => !string.IsNullOrWhiteSpace(file.Sha256)
                        && Uri.TryCreate(file.Source, UriKind.Absolute, out var source)
                        && source.Scheme == Uri.UriSchemeHttps),
                InstallDirectory = directory,
                ModelsRoot = modelsRoot,
                Status = MapStatus(manifest.Status),
                StoredBytes = manifest.DownloadedBytes,
                LastVerifiedAt = manifest.VerifiedAt,
                Origin = ManagedModelOrigins.ExistingManifest,
                Consumers =
                [
                    Consumer("web-search", "Интернет-поиск", "tool")
                ],
                Files = manifest.Files.Select(file => new ManagedModelArtifactFile
                {
                    RelativePath = file.File,
                    SourceUrl = file.Source,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                    Purpose = manifest.ToolKind
                }).ToList()
            };
            _store.Upsert(card);
        }
        catch
        {
            // Import of one legacy manifest must not block the remaining library.
        }
    }

    private void ImportComponentModels()
    {
        foreach (var status in _componentManager.GetStatus()
                     .Where(status => status.Entry.Id.StartsWith("model.", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = status.Entry;
            var installPath = status.Record.InstallPath;
            var installDirectory = string.IsNullOrWhiteSpace(installPath)
                ? string.Empty
                : File.Exists(installPath) ? Path.GetDirectoryName(installPath) ?? string.Empty : installPath;
            var relativePath = string.IsNullOrWhiteSpace(entry.FileName)
                ? entry.Id + ".artifact"
                : Path.GetFileName(entry.FileName);
            var card = new ManagedModelArtifactCard
            {
                ModelArtifactId = "component-" + SanitizeId(entry.Id),
                Family = entry.Name,
                DisplayName = entry.Name,
                Role = entry.Id.Contains("vision", StringComparison.OrdinalIgnoreCase)
                    ? ManagedModelRoles.Vision
                    : ManagedModelRoles.Tool,
                Provider = "Component catalog",
                RepositoryId = entry.Source,
                Revision = entry.Version,
                Format = Path.GetExtension(entry.FileName).TrimStart('.'),
                Architecture = entry.Architecture,
                License = entry.License,
                SourcePage = entry.Source,
                IsManaged = true,
                CanRemoveFiles = false,
                SupportsDirectDownload = false,
                InstallDirectory = installDirectory,
                ModelsRoot = AppDataPaths.ComponentsDirectory,
                Status = status.IsAvailable ? ManagedModelStatuses.Installed : ManagedModelStatuses.NotInstalled,
                StoredBytes = status.Record.DownloadedBytes,
                LastVerifiedAt = status.Record.VerifiedAt,
                LastError = status.Record.LastError,
                Origin = ManagedModelOrigins.ExistingManifest,
                Consumers =
                [
                    Consumer("sandbox-components", "Песочница и инструменты", "component")
                ],
                Files =
                [
                    new ManagedModelArtifactFile
                    {
                        RelativePath = relativePath,
                        SourceUrl = entry.DownloadUrl,
                        SizeBytes = entry.DownloadSizeBytes,
                        Sha256 = entry.Sha256,
                        Purpose = "component_model"
                    }
                ]
            };
            _store.Upsert(card);
        }
    }

    private void ImportExternalGguf(StorageSettings settings)
    {
        var managedPaths = _store.LoadAll()
            .Where(card => card.IsManaged && !string.IsNullOrWhiteSpace(card.InstallDirectory))
            .SelectMany(card => card.Files.Select(file => SafeCombine(card.InstallDirectory, file.RelativePath)))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetModelRoots(settings).Where(Directory.Exists))
        {
            foreach (var path in SafeEnumerate(root, "*.gguf"))
            {
                var fullPath = Path.GetFullPath(path);
                if (managedPaths.Contains(fullPath))
                {
                    continue;
                }
                var info = new FileInfo(fullPath);
                var card = new ManagedModelArtifactCard
                {
                    Family = Path.GetFileNameWithoutExtension(info.Name),
                    DisplayName = info.Name,
                    Role = ManagedModelRoles.External,
                    Provider = "Local file",
                    RepositoryId = fullPath,
                    Format = "GGUF",
                    IsManaged = false,
                    CanRemoveFiles = false,
                    SupportsDirectDownload = false,
                    InstallDirectory = info.DirectoryName ?? string.Empty,
                    ModelsRoot = root,
                    Status = ManagedModelStatuses.External,
                    StoredBytes = info.Length,
                    Origin = ManagedModelOrigins.ExternalDiscovery,
                    Files =
                    [
                        new ManagedModelArtifactFile
                        {
                            RelativePath = info.Name,
                            SizeBytes = info.Length,
                            Purpose = "external_file"
                        }
                    ]
                };
                card.ModelArtifactId = ManagedModelLibraryStore.CreateStableId(card);
                _store.Upsert(card);
            }
        }
    }

    private static void ApplyFastFileState(
        ManagedModelArtifactCard candidate,
        ManagedModelArtifactCard? existing)
    {
        candidate.LastError = existing?.LastError ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate.InstallDirectory))
        {
            candidate.Status = ManagedModelStatuses.NotInstalled;
            return;
        }
        var exactBytes = 0L;
        var hasPartial = false;
        var allExact = true;
        foreach (var file in candidate.Files)
        {
            var path = SafeCombine(candidate.InstallDirectory, file.RelativePath);
            var info = new FileInfo(path);
            if (info.Exists)
            {
                exactBytes += info.Length;
                allExact &= info.Length == file.SizeBytes;
            }
            else
            {
                allExact = false;
                var storedBytes = SegmentedModelFileDownloader.GetStoredBytes(path, file.SizeBytes);
                hasPartial |= storedBytes > 0;
                if (storedBytes > 0)
                {
                    exactBytes += storedBytes;
                }
            }
        }
        candidate.StoredBytes = exactBytes;
        if (allExact)
        {
            var legacyCore = string.Equals(candidate.ModelArtifactId, ManagedModelCatalog.CoreArtifactId, StringComparison.Ordinal)
                ? LoadCoreManifest(candidate.InstallDirectory)
                : null;
            candidate.LastVerifiedAt = existing?.LastVerifiedAt ?? legacyCore?.VerifiedAt;
            candidate.RuntimeVerifiedAt = existing?.RuntimeVerifiedAt;
            var unchangedSinceVerification = candidate.LastVerifiedAt is not null
                && candidate.Files.All(file =>
                {
                    var path = SafeCombine(candidate.InstallDirectory, file.RelativePath);
                    var info = new FileInfo(path);
                    var previous = existing?.Files.FirstOrDefault(item => string.Equals(
                        item.RelativePath,
                        file.RelativePath,
                        StringComparison.OrdinalIgnoreCase));
                    return previous?.VerifiedLastWriteTimeUtc is { } verifiedTime
                        ? previous.VerifiedSizeBytes == info.Length
                            && verifiedTime.UtcDateTime == info.LastWriteTimeUtc
                        : info.LastWriteTimeUtc <= candidate.LastVerifiedAt.Value.UtcDateTime;
                });
            candidate.Status = unchangedSinceVerification
                ? string.Equals(legacyCore?.Status, "installed", StringComparison.OrdinalIgnoreCase)
                    || existing?.Status == ManagedModelStatuses.Installed
                    ? ManagedModelStatuses.Installed
                    : existing?.Status ?? ManagedModelStatuses.NeedsVerification
                : ManagedModelStatuses.NeedsVerification;
            return;
        }
        candidate.Status = hasPartial
            ? ManagedModelStatuses.Paused
            : exactBytes > 0
                ? existing?.Status == ManagedModelStatuses.SourceUnavailable
                    ? ManagedModelStatuses.SourceUnavailable
                    : ManagedModelStatuses.Corrupted
                : existing?.Status is ManagedModelStatuses.FilesRemoved or ManagedModelStatuses.SourceUnavailable
                    ? existing.Status
                    : ManagedModelStatuses.NotInstalled;
    }

    private static string MapStatus(string status) => status.ToLowerInvariant() switch
    {
        "installed" => ManagedModelStatuses.Installed,
        "partial" => ManagedModelStatuses.Paused,
        "downloaded_verified" => ManagedModelStatuses.NeedsVerification,
        "runtime_incompatible" => ManagedModelStatuses.RuntimeIncompatible,
        "invalid" => ManagedModelStatuses.Corrupted,
        _ => ManagedModelStatuses.NotInstalled
    };

    private static CoreModelManifest? LoadCoreManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "core-model.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<CoreModelManifest>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static (string Repository, string Revision) ParseHuggingFaceSource(string source, string fallbackRepository)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return (fallbackRepository, string.Empty);
        }
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var resolve = Array.FindIndex(parts, part => string.Equals(part, "resolve", StringComparison.OrdinalIgnoreCase));
        return resolve >= 2 && resolve + 1 < parts.Length
            ? ($"{parts[0]}/{parts[1]}", parts[resolve + 1])
            : (fallbackRepository, string.Empty);
    }

    private static IReadOnlyList<string> GetModelRoots(StorageSettings settings) => settings.Models.Locations
        .Select(location => location.Path?.Trim())
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeId(string value) => string.Concat(value.Select(character =>
        char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));

    private static bool IsImmutableRevision(string revision) =>
        revision.Length == 40 && revision.All(Uri.IsHexDigit);

    private static ManagedModelConsumer Consumer(string id, string name, string kind) => new()
    {
        Id = id,
        DisplayName = name,
        Kind = kind
    };
}
