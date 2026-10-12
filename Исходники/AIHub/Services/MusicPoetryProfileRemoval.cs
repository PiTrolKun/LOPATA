using System.IO;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>Only pinned-manifest files of dedicated song-chat profiles are removable here.</summary>
internal sealed class MusicPoetryProfileRemoval(ManagedModelLibraryStore store, IModelUsageGuard guard)
{
    private static ManagedModelArtifactCard[] Cards(MusicPoetryModelCandidate candidate, StorageSettings settings)
    {
        var profile = MusicPoetryProfileCatalog.Find(candidate.Id)
            ?? throw new InvalidOperationException("Music.Poetry.ModelUnsupported");
        return settings.Models.Locations.Select(l => l.Path).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => MusicPoetryProfileCatalog.Card(profile, root)).ToArray();
    }

    public static bool HasFiles(MusicPoetryModelCandidate candidate, StorageSettings settings)
    {
        try { return Cards(candidate, settings).Any(c => Paths(c).Any(File.Exists)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static IEnumerable<string> Paths(ManagedModelArtifactCard card) => card.Files.SelectMany(f => {
        var path = Path.GetFullPath(Path.Combine(card.InstallDirectory, f.RelativePath));
        var install = Path.GetFullPath(card.InstallDirectory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!path.StartsWith(install, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe model file path.");
        return new[] { path }.Concat(SegmentedModelFileDownloader.GetPartialArtifactPaths(path));
    });

    public long Remove(MusicPoetryModelCandidate candidate, StorageSettings settings)
    {
        var cards = Cards(candidate, settings).Where(c => Paths(c).Any(File.Exists)).ToArray();
        var previous = store.Load(candidate.LicenseId);
        if (guard.IsActive(candidate.LicenseId) || previous is { IsPinned: true } or { IsSystem: true }
            or { IsManaged: false } or { CanRemoveFiles: false }
            or { Status: ManagedModelStatuses.InUse } || previous?.Consumers.Any(c => c.Id != "music-poetry") == true)
            throw new InvalidOperationException("Music.Poetry.Selection.RemoveBlocked");
        var foreign = store.LoadAll().Where(c => c.ModelArtifactId != candidate.LicenseId).ToArray();
        // Preflight all roots before the first deletion. Shared artifacts and links are protected.
        foreach (var card in cards)
        {
            SafeUpdatePath.RejectLinks(card.ModelsRoot); SafeUpdatePath.RejectLinks(card.InstallDirectory);
            var targets = Paths(card).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in targets) SafeUpdatePath.RejectLinks(path);
            if (foreign.Any(other => !string.IsNullOrWhiteSpace(other.InstallDirectory)
                && other.Files.Any(f => targets.Contains(Path.GetFullPath(Path.Combine(other.InstallDirectory, f.RelativePath))))))
                throw new InvalidOperationException("Music.Poetry.Selection.RemoveBlocked");
        }
        long bytes = 0;
        foreach (var card in cards)
        {
            if (guard.IsActive(candidate.LicenseId)) throw new InvalidOperationException("Music.Poetry.Selection.RemoveBlocked");
            store.Upsert(card);
            store.ResetMusicInstallationReceipt(card.ModelArtifactId, ManagedModelStatuses.NeedsVerification, card.StoredBytes);
            bytes += new ManagedModelRemovalService(store, guard).RemoveFiles(card.ModelArtifactId, includePartialFiles: true).RemovedBytes;
            store.ResetMusicInstallationReceipt(card.ModelArtifactId, ManagedModelStatuses.FilesRemoved, 0);
            // Preserve unknown files; prune only an empty, validated profile directory.
            SafeUpdatePath.RejectLinks(card.InstallDirectory);
            if (Directory.Exists(card.InstallDirectory) && !Directory.EnumerateFileSystemEntries(card.InstallDirectory).Any())
                Directory.Delete(card.InstallDirectory);
        }
        return bytes;
    }
}
