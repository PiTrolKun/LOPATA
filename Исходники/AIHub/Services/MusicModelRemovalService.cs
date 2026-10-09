using System.IO;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal sealed record MusicRemovalFile(string Path, long Bytes, long WriteTicks);
internal sealed record MusicRemovalPlan(string Variation, IReadOnlyList<string> Roots,
    IReadOnlyList<MusicRemovalFile> Files, IReadOnlyList<string> Components,
    IReadOnlyList<string> Preserved, IReadOnlyList<string> Directories)
{
    public long Bytes => Files.Sum(f => f.Bytes);
}

/// <summary>Removes an installed music package, never a storage tree or user results.</summary>
internal sealed class MusicModelRemovalService(ManagedModelLibraryStore store, IModelUsageGuard guard,
    Func<string, string, IReadOnlyList<ManagedModelArtifactCard>>? catalog = null,
    Func<string, IReadOnlyList<string>>? cacheDirectories = null)
{
    private readonly Func<string, string, IReadOnlyList<ManagedModelArtifactCard>> _catalog = catalog ?? MusicModelVariants.Cards;
    private readonly Func<string, IReadOnlyList<string>> _caches = cacheDirectories ?? CacheDirectories;

    internal static IReadOnlyList<string> CacheDirectories(string variation)
    {
        var music = Path.Combine(AppDataPaths.BaseDirectory, "Music");
        return variation switch {
            MusicAceCatalog.Variation => [Path.Combine(music, "Python", MusicAceCatalog.RuntimeRevision), MusicAceSource.DirectoryPath],
            MusicDiffRhythmCatalog.Variation => [Path.Combine(music, "Python", MusicDiffRhythmCatalog.RuntimeRevision), MusicDiffRhythmSource.DirectoryPath],
            MusicHeartMuLaCatalog.Variation => [Path.Combine(music, "Python", MusicHeartMuLaCatalog.RuntimeRevision), MusicHeartMuLaSource.DirectoryPath],
            _ => []
        };
    }

    public bool HasFiles(string variation, IReadOnlyList<string> roots)
    {
        if (!MusicModelVariants.Supported(variation)) return false;
        // Cheap UI inventory only. Full path and ownership checks happen in Preview.
        try {
            var cards = roots.Where(r => !string.IsNullOrWhiteSpace(r)).SelectMany(root => _catalog(root, variation)).ToArray();
            return cards.Any(c => c.Files.Any(f => HasArtifact(Path.Combine(c.InstallDirectory, f.RelativePath))))
                || cards.Select(c => c.ModelArtifactId).Distinct().Any(id => store.Load(id) is { } previous
                    && roots.Any(r => Inside(r, previous.InstallDirectory))
                    && previous.Files.Any(f => HasArtifact(Path.Combine(previous.InstallDirectory, f.RelativePath))))
                || _caches(variation).Any(cache => Directory.Exists(cache) || HasStages(cache));
        }
        catch { return false; }
    }

    private static bool HasArtifact(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        return File.Exists(path) || SegmentedModelFileDownloader.GetPartialArtifactPaths(path).Any(File.Exists);
    }
    private static bool HasStages(string cache)
    {
        var parent = Path.GetDirectoryName(cache)!;
        SafeUpdatePath.RejectLinks(parent);
        return Directory.Exists(parent) && Directory.EnumerateDirectories(parent, Path.GetFileName(cache) + ".*.partial")
            .Any(stage => Guid.TryParseExact(Path.GetFileName(stage)[(Path.GetFileName(cache).Length + 1)..^8], "N", out _));
    }

    public MusicRemovalPlan Preview(string variation, IReadOnlyList<string> roots)
    {
        if (!MusicModelVariants.Supported(variation)) throw new InvalidDataException("Unsupported music model.");
        var fullRoots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fullRoots.Length == 0) throw new InvalidDataException("No model storage configured.");
        var cards = fullRoots.SelectMany(r => _catalog(r, variation)).ToList();
        var ids = cards.Select(c => c.ModelArtifactId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = store.LoadAll();
        foreach (var card in registered.Where(c => ids.Contains(c.ModelArtifactId))) {
            if (!card.IsManaged || !card.CanRemoveFiles || card.IsPinned || card.IsSystem || card.Status == ManagedModelStatuses.InUse)
                throw new InvalidOperationException("Registered model is protected or active.");
            foreach (var file in card.Files) SafeUpdatePath.RejectLinks(FilePath(card.InstallDirectory, file.RelativePath));
        }
        // Registered earlier revisions/locations remain removable within configured storage only.
        foreach (var card in registered.Where(c => ids.Contains(c.ModelArtifactId))) {
            if (!cards.Any(c => Same(c.InstallDirectory, card.InstallDirectory))) cards.Add(card);
        }
        var foreign = registered.Where(c => !ids.Contains(c.ModelArtifactId)).ToArray();
        var files = new Dictionary<string, MusicRemovalFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new HashSet<string>(); var preserved = new HashSet<string>();
        foreach (var card in cards) {
            var install = Path.GetFullPath(card.InstallDirectory);
            if (!card.IsManaged || !card.CanRemoveFiles || card.IsPinned || card.IsSystem
                || guard.IsActive(card.ModelArtifactId) || card.Status == ManagedModelStatuses.InUse
                || registered.Any(c => c.ModelArtifactId == card.ModelArtifactId && (c.IsPinned || c.Status == ManagedModelStatuses.InUse)))
                throw new InvalidOperationException("Model is active, pinned or not managed by LOPATA.");
            if (!fullRoots.Any(r => Inside(r, install)) || !Same(card.ModelsRoot, fullRoots.First(r => Inside(r, install))))
                throw new InvalidDataException("Model path escapes configured storage.");
            SafeUpdatePath.RejectLinks(install);
            var shared = foreign.Any(other => Shares(card, other)) || registered.Any(c => c.ModelArtifactId == card.ModelArtifactId
                && c.Consumers.Any(consumer => consumer.Id != ScenarioNavigationCatalog.Music));
            if (shared) { preserved.Add(card.DisplayName); continue; }
            var before = files.Count;
            foreach (var file in card.Files) {
                var path = FilePath(install, file.RelativePath);
                Add(path);
                foreach (var partial in SegmentedModelFileDownloader.GetPartialArtifactPaths(path)) Add(partial);
                for (var parent = Path.GetDirectoryName(path); parent is not null && Inside(install, parent); parent = Path.GetDirectoryName(parent))
                    directories.Add(parent);
            }
            if (files.Count > before) components.Add(card.DisplayName);
            directories.Add(install);
        }
        foreach (var cache in _caches(variation)) {
            SafeUpdatePath.RejectLinks(cache);
            // Keep caches which have explicitly registered other consumers.
            if (preserved.Count > 0 || foreign.Any(c => Same(c.InstallDirectory, cache) || Inside(cache, c.InstallDirectory))) {
                preserved.Add(cache); continue;
            }
            if (Directory.Exists(cache)) components.Add(cache);
            AddTree(cache);
            var parent = Path.GetDirectoryName(cache)!;
            if (Directory.Exists(parent)) {
                SafeUpdatePath.RejectLinks(parent);
                foreach (var stage in Directory.EnumerateDirectories(parent, Path.GetFileName(cache) + ".*.partial")) {
                    var suffix = Path.GetFileName(stage)[(Path.GetFileName(cache).Length + 1)..^8];
                    if (Guid.TryParseExact(suffix, "N", out _)) { components.Add(stage); AddTree(stage); }
                }
            }
        }
        return new(variation, fullRoots, files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            components.ToArray(), preserved.ToArray(), directories.OrderByDescending(p => p.Length).ToArray());

        void Add(string path) {
            SafeUpdatePath.RejectLinks(path);
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            files.TryAdd(info.FullName, new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks));
            var parent = info.DirectoryName!;
            // Prune only empty subdirectories, never roots or unknown files.
            directories.Add(parent);
        }
        void AddTree(string root) {
            if (!Directory.Exists(root)) return;
            SafeUpdatePath.RejectLinks(root); directories.Add(root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root)) {
                SafeUpdatePath.RejectLinks(entry);
                if (Directory.Exists(entry)) AddTree(entry); else Add(entry);
            }
        }
    }

    public long Remove(MusicRemovalPlan confirmed)
    {
        // Dialog is not a lock: re-check ownership, pins, usage, links and exact inventory.
        var current = Preview(confirmed.Variation, confirmed.Roots);
        if (!current.Files.SequenceEqual(confirmed.Files) || !current.Preserved.Order().SequenceEqual(confirmed.Preserved.Order()))
            throw new InvalidOperationException("Removal plan changed. Review and confirm again.");
        var ids = current.Roots.SelectMany(r => _catalog(r, current.Variation)).Select(c => c.ModelArtifactId).ToHashSet();
        var registered = store.LoadAll();
        var affected = registered.Where(c => ids.Contains(c.ModelArtifactId)
            && !registered.Where(other => !ids.Contains(other.ModelArtifactId)).Any(other => Shares(c, other))
            && !c.Consumers.Any(consumer => consumer.Id != ScenarioNavigationCatalog.Music)).ToArray();
        // Invalidate before mutation so an interrupted operation cannot retain a ready receipt.
        foreach (var card in affected) SaveStatus(card, ManagedModelStatuses.NeedsVerification);
        long removed = 0;
        var index = 0;
        foreach (var file in current.Files) {
            if (index++ % 256 == 0 && ids.Any(guard.IsActive)) throw new InvalidOperationException("Model became active during removal.");
            SafeUpdatePath.RejectLinks(file.Path);
            if (!File.Exists(file.Path)) continue;
            var info = new FileInfo(file.Path);
            if (info.Length != file.Bytes || info.LastWriteTimeUtc.Ticks != file.WriteTicks)
                throw new IOException("Model file changed during removal.");
            File.Delete(file.Path); removed += file.Bytes;
        }
        foreach (var directory in current.Directories) {
            SafeUpdatePath.RejectLinks(directory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        foreach (var card in affected) SaveStatus(card, ManagedModelStatuses.FilesRemoved);
        return removed;
    }

    private void SaveStatus(ManagedModelArtifactCard card, string status)
    {
        card.Status = status; card.StoredBytes = card.Files.Sum(f => {
            var path = FilePath(card.InstallDirectory, f.RelativePath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        });
        store.ResetMusicInstallationReceipt(card.ModelArtifactId, status, card.StoredBytes);
    }
    private static bool Shares(ManagedModelArtifactCard card, ManagedModelArtifactCard other)
    {
        if (string.IsNullOrWhiteSpace(other.InstallDirectory)) return false;
        var paths = other.Files.Select(f => FilePath(other.InstallDirectory, f.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return card.Files.Any(f => paths.Contains(FilePath(card.InstallDirectory, f.RelativePath)));
    }
    private static bool Same(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string root, string path) => !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(path)
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string FilePath(string install, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')
            || relative.Split('\\', '/').Any(s => s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.')))
            throw new InvalidDataException("Unsafe model manifest path.");
        var path = Path.GetFullPath(Path.Combine(install, relative));
        if (!Inside(install, path)) throw new InvalidDataException("Model file escapes installation.");
        return path;
    }
}
