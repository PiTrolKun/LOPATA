using AIHub.Models;
using Lopata.Updates;
using System.IO;

namespace AIHub.Services;

public interface IModelUsageGuard
{
    bool IsActive(string modelArtifactId);
}

public sealed class NullModelUsageGuard : IModelUsageGuard
{
    public bool IsActive(string modelArtifactId) => false;
}

public sealed class DelegateModelUsageGuard(Func<string, bool> predicate) : IModelUsageGuard
{
    public bool IsActive(string modelArtifactId) => predicate(modelArtifactId);
}

internal sealed record ManagedModelRemovalFile(string Path, long Bytes, long WriteTicks);

public sealed class ManagedModelRemovalService
{
    private const string RetiredFullTurboArtifactId = "generation-qwen-image-21-turbo-full";
    private const string RetiredFullTurboPreparedEncoder = "text_encoder/lopata-sdcpp-bf16-v1.safetensors";
    private readonly ManagedModelLibraryStore _store;
    private readonly IModelUsageGuard _usageGuard;

    public ManagedModelRemovalService(
        ManagedModelLibraryStore store,
        IModelUsageGuard? usageGuard = null)
    {
        _store = store;
        _usageGuard = usageGuard ?? new NullModelUsageGuard();
    }

    public ManagedModelRemovalResult RemoveFiles(string modelArtifactId, bool includePartialFiles)
    {
        var card = _store.Load(modelArtifactId)
            ?? throw new InvalidOperationException("The model card is not registered in the LOPATA library.");
        EnsureRemovalAllowed(card);
        var root = Path.GetFullPath(card.ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var installRoot = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!installRoot.StartsWith(root, StringComparison.OrdinalIgnoreCase) || installRoot == root)
        {
            throw new InvalidOperationException("Refusing to remove files outside the configured model storage.");
        }

        EnsureNoReparsePoint(root, installRoot);
        var removed = new List<string>();
        long removedBytes = 0;
        foreach (var file in card.Files)
        {
            var path = GetContainedPath(installRoot, file.RelativePath);
            removedBytes += DeleteExactFile(path, removed);
            if (includePartialFiles)
            {
                foreach (var partialPath in SegmentedModelFileDownloader.GetPartialArtifactPaths(path))
                {
                    removedBytes += DeleteExactFile(partialPath, removed);
                }
            }
        }
        // Older installations can still remove the retired bundle from the model library.
        if (card.ModelArtifactId == RetiredFullTurboArtifactId)
        {
            var prepared = GetContainedPath(installRoot, RetiredFullTurboPreparedEncoder);
            Lopata.Updates.SafeUpdatePath.RejectLinks(prepared);
            Lopata.Updates.SafeUpdatePath.RejectLinks(prepared + ".tmp");
            removedBytes += DeleteExactFile(prepared, removed);
            removedBytes += DeleteExactFile(prepared + ".tmp", removed);
        }

        card.StoredBytes = 0;
        card.Status = ManagedModelStatuses.FilesRemoved;
        card.LastVerifiedAt = null;
        card.RuntimeVerifiedAt = null;
        card.LastError = string.Empty;
        _store.Upsert(card);
        return new ManagedModelRemovalResult(removedBytes, removed);
    }

    internal static ManagedModelRemovalFile[] InspectScenarioFiles(ManagedModelArtifactCard card)
    {
        var root = Path.GetFullPath(card.ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var install = Path.GetFullPath(card.InstallDirectory);
        if (!install.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || ManagedModelPathIdentity.SameDirectory(root, install))
            throw new InvalidDataException("Refusing removal outside model storage.");
        SafeUpdatePath.RejectLinks(install);
        var paths = card.Files.Select(file => SafeUpdatePath.Resolve(install, file.RelativePath))
            .SelectMany(path => new[] { path }.Concat(SegmentedModelFileDownloader.GetPartialArtifactPaths(path)));
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                SafeUpdatePath.RejectLinks(path);
                return new FileInfo(path);
            })
            .Where(file => file.Exists)
            .Select(file => new ManagedModelRemovalFile(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks))
            .ToArray();
    }

    internal void EnsureScenarioRemovalAllowed(ManagedModelArtifactCard card)
    {
        EnsureRemovalAllowed(card);
        var registered = _store.Load(card.ModelArtifactId);
        if (card.Origin != ManagedModelOrigins.PredefinedScenario || card.IsSystem
            || registered is { IsSystem: true }
            || registered is not null && registered.Origin != ManagedModelOrigins.PredefinedScenario)
            throw new InvalidOperationException("The scenario component is protected.");
        if (registered is not null) EnsureRemovalAllowed(registered);
    }

    internal long RemoveReviewedScenarioFiles(ManagedModelArtifactCard card, IReadOnlyList<ManagedModelRemovalFile> confirmed)
    {
        EnsureScenarioRemovalAllowed(card);
        if (!InspectScenarioFiles(card).SequenceEqual(confirmed))
            throw new InvalidOperationException("The model files changed after confirmation.");
        var registered = _store.Load(card.ModelArtifactId);
        var resetReceipt = registered is not null
            && ManagedModelPathIdentity.SameDirectory(card.InstallDirectory, registered.InstallDirectory);
        ComponentAudit.Invalidate(card.InstallDirectory);
        if (resetReceipt)
            _store.ResetInstallationReceipt(card.ModelArtifactId, ManagedModelStatuses.NeedsVerification, registered!.StoredBytes);
        var removed = new List<string>();
        long bytes = 0;
        try
        {
            foreach (var file in confirmed)
            {
                if (_usageGuard.IsActive(card.ModelArtifactId))
                    throw new InvalidOperationException("The component became active.");
                SafeUpdatePath.RejectLinks(file.Path);
                var current = new FileInfo(file.Path);
                if (!current.Exists || current.Length != file.Bytes || current.LastWriteTimeUtc.Ticks != file.WriteTicks)
                    throw new IOException("A model file changed during removal.");
                bytes += DeleteExactFile(file.Path, removed);
            }
        }
        finally
        {
            if (resetReceipt)
            {
                var remaining = InspectScenarioFiles(card);
                var status = remaining.Length == 0 ? ManagedModelStatuses.FilesRemoved : ManagedModelStatuses.NeedsVerification;
                _store.ResetInstallationReceipt(card.ModelArtifactId, status, remaining.Sum(file => file.Bytes));
            }
        }
        return bytes;
    }

    // Explicit, reviewed removal for predefined scenario packages. The pinned
    // catalog definition stays pinned; arbitrary paths and user-owned cards are rejected.
    internal ManagedModelRemovalResult RemoveScenarioFiles(string modelArtifactId,
        IReadOnlyCollection<string> relativePaths, bool includePartialFiles)
    {
        var card = _store.Load(modelArtifactId) ?? throw new InvalidOperationException("Unknown scenario component.");
        if (!card.IsManaged || card.IsSystem || card.Origin != ManagedModelOrigins.PredefinedScenario
            || _usageGuard.IsActive(modelArtifactId) || card.Status == ManagedModelStatuses.InUse)
            throw new InvalidOperationException("The scenario component is protected or in use.");
        var root = Path.GetFullPath(card.ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var install = Path.GetFullPath(card.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!install.StartsWith(root, StringComparison.OrdinalIgnoreCase) || install == root)
            throw new InvalidOperationException("Refusing removal outside model storage.");
        var allowed = card.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (relativePaths.Count == 0 || relativePaths.Any(p => !allowed.Contains(p)))
            throw new InvalidDataException("Only reviewed manifest files may be removed.");
        foreach(var relative in relativePaths) Lopata.Updates.SafeUpdatePath.ValidateRelative(relative);
        var paths = relativePaths.Select(p => GetContainedPath(install, p)).ToArray();
        var targets = paths.SelectMany(p => includePartialFiles
            ? new[] { p }.Concat(SegmentedModelFileDownloader.GetPartialArtifactPaths(p)) : [p]).ToArray();
        foreach (var path in targets) {
            if (!path.StartsWith(install, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe partial file.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        }
        var removed = new List<string>(); long bytes = 0;
        try {
            foreach (var path in targets) {
                if (_usageGuard.IsActive(modelArtifactId)) throw new InvalidOperationException("Component became active.");
                bytes += DeleteExactFile(path, removed);
            }
        }
        finally {
            if (removed.Count > 0) {
                ComponentAudit.Invalidate(card.InstallDirectory);
                card.StoredBytes = card.Files.Sum(f => File.Exists(GetContainedPath(install, f.RelativePath))
                    ? new FileInfo(GetContainedPath(install, f.RelativePath)).Length : 0);
                card.Status = card.StoredBytes == 0 ? ManagedModelStatuses.FilesRemoved : ManagedModelStatuses.NeedsVerification;
                _store.ResetInstallationReceipt(card.ModelArtifactId,card.Status,card.StoredBytes);
            }
        }
        return new(bytes, removed);
    }

    private void EnsureRemovalAllowed(ManagedModelArtifactCard card)
    {
        if (!card.IsManaged || !card.CanRemoveFiles)
        {
            throw new InvalidOperationException("LOPATA does not own these model files and cannot remove them.");
        }
        if (card.IsPinned)
        {
            throw new InvalidOperationException("The model is pinned. Unpin it before removing files.");
        }
        if (_usageGuard.IsActive(card.ModelArtifactId) || card.Status == ManagedModelStatuses.InUse)
        {
            throw new InvalidOperationException("The model is used by an active session and cannot be removed.");
        }
        if (string.IsNullOrWhiteSpace(card.ModelsRoot) || string.IsNullOrWhiteSpace(card.InstallDirectory))
        {
            throw new InvalidOperationException("The managed model path is not available.");
        }
    }

    private static long DeleteExactFile(string path, ICollection<string> removed)
    {
        if (!File.Exists(path))
        {
            return 0;
        }
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Refusing to remove a model file through a reparse point.");
        }
        var bytes = info.Length;
        File.Delete(path);
        removed.Add(path);
        return bytes;
    }

    private static string GetContainedPath(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("The model manifest contains an unsafe file path.");
        }
        var path = Path.GetFullPath(Path.Combine(installRoot, relativePath));
        if (!path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The model file path escapes the managed model directory.");
        }
        return path;
    }

    private static void EnsureNoReparsePoint(string modelsRoot, string installRoot)
    {
        var current = new DirectoryInfo(installRoot.TrimEnd(Path.DirectorySeparatorChar));
        var boundary = modelsRoot.TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null
               && current.FullName.StartsWith(modelsRoot, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(current.FullName, boundary, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Refusing to remove model files through a junction or symbolic link.");
            }
            current = current.Parent;
        }
    }
}
