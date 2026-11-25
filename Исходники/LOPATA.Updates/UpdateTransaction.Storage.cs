namespace Lopata.Updates;

public sealed record UpdateStorageFile(string Path, long Size, string Sha256);
public sealed record UpdateStorageGroup(string Id, DateTime Date, IReadOnlyList<UpdateStorageFile> Files)
{
    public long Size => Files.Sum(file => file.Size);
}
public sealed record UpdateStoragePlan(long Bytes, long Threshold, IReadOnlyList<UpdateStorageGroup> Groups, long Reserved = 0)
{
    public long Reclaimable => Groups.Sum(group => group.Size);
}

public sealed partial class UpdateTransaction
{
    public const long DefaultStorageThreshold = 10_000_000_000;

    public UpdateStoragePlan PlanStorageCleanup(string workspace, long threshold = DefaultStorageThreshold)
    {
        using var lease = Acquire();
        return PlanStorageCleanupLocked(workspace, threshold);
    }

    public async Task<long> CleanStorageAsync(string workspace, UpdateStoragePlan consent, CancellationToken token = default)
    {
        using var lease = Acquire();
        var allowed = PlanStorageCleanupLocked(workspace, consent.Threshold);
        var selected = consent.Groups.SelectMany(group => group.Files).ToArray();
        if (!selected.SequenceEqual(allowed.Groups.SelectMany(group => group.Files)) ||
            selected.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new IOException("The update storage plan has changed.");
        long removed = 0;
        foreach (var file in selected)
        {
            token.ThrowIfCancellationRequested();
            SafeUpdatePath.RejectLinks(file.Path);
            if (!await UpdatePlanner.MatchesAsync(file.Path, file.Size, file.Sha256, token)) continue;
            File.Delete(file.Path);
            removed += file.Size;
        }
        return removed;
    }

    private UpdateStoragePlan PlanStorageCleanupLocked(string workspace, long threshold)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var stateParent = Path.GetDirectoryName(_stateDirectory);
        var legacyRoot = stateParent is not null && Path.GetFileName(stateParent) == "Installations"
            ? Path.GetDirectoryName(stateParent)! : _stateDirectory;
        var candidates = new[] { legacyRoot, workspace }
            .Concat(_roots.RootIds.Select(root => _roots.Internal(root, ".lopata-update")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long total = candidates.Where(directory => !candidates.Any(other => !string.Equals(other, directory, StringComparison.OrdinalIgnoreCase)
            && directory.StartsWith(Path.TrimEndingDirectorySeparator(other) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .Sum(DirectorySize);
        if (total <= threshold || ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) }) return new(total, threshold, []);
        // Pending files and resumable package parts remain available until the pending operation is resolved.
        if (new PreparedUpdateStore(_stateDirectory, _keys).Read() is not null) return new(total, threshold, []);

        var installed = SignedManifest.Read(File.ReadAllBytes(InstalledManifestPath)).Verify(_keys);
        var history = HealthyTransactions();
        var chain = RollbackChain(ProtectedRollbackVersions);
        var reserved = chain.Sum(journal => _roots.RootIds.Sum(root => DirectorySize(
            _roots.Internal(root, $".lopata-update/{journal.Id}/backup"))));
        var protectedJournals = chain.Select(x => x.Id).Append(ReadJournal()?.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestAuthority = new List<UpdateManifest> { installed };
        manifestAuthority.AddRange(chain.SelectMany(j => new[] { j.Target.Verify(_keys), j.Previous!.Verify(_keys) }));
        var keepPackages = manifestAuthority.SelectMany(m => m.Packages).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepVersions = manifestAuthority.Select(m => UpdateManifest.NumericVersion(m.Version)).ToHashSet();
        var keepWorkers = manifestAuthority.SelectMany(m => m.Files)
            .Where(IsUpdater).Select(f => f.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<UpdateStorageGroup>();
        foreach (var journal in history.OrderBy(x => File.GetLastWriteTimeUtc(Path.Combine(HistoryDirectory, x.Id + ".json"))))
        {
            if (protectedJournals.Contains(journal.Id)) continue;
            var files = new List<UpdateStorageFile>();
            foreach (var op in journal.Operations.Where(op => op.Existed && op.Action != UpdateAction.Keep))
            {
                var previous = op.Previous!;
                Add(files, TransactionFile(journal.Id, previous, "backup"), previous.Size, previous.Sha256);
            }
            foreach (var manifest in new[] { journal.Target.Verify(_keys), journal.Previous!.Verify(_keys) })
                AddManifest(files, manifest);
            if (files.Count > 0)
                groups.Add(new(journal.Id, File.GetLastWriteTimeUtc(Path.Combine(HistoryDirectory, journal.Id + ".json")), files));
        }
        // Transition builds already pin older signed manifests for Collection ownership.
        // These can identify legacy caches, but do not prove ownership of an old rollback folder.
        foreach (var (manifest, remembered) in CollectionManifests().OrderBy(x => x.Remembered))
        {
            if (keepVersions.Contains(UpdateManifest.NumericVersion(manifest.Version))) continue;
            var files = new List<UpdateStorageFile>();
            AddManifest(files, manifest);
            if (files.Count > 0) groups.Add(new(manifest.Version, remembered, files));
        }
        return new(total, threshold, groups.OrderBy(g => g.Date).ThenBy(g => g.Id, StringComparer.Ordinal)
            .Take((groups.Count + 1) / 2).ToArray(), reserved);

        void AddManifest(List<UpdateStorageFile> files, UpdateManifest manifest)
        {
            var numeric = UpdateManifest.NumericVersion(manifest.Version);
            foreach (var directory in new[] { _stateDirectory, workspace }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var package in manifest.Packages.Where(p => !keepPackages.Contains(p.Id)))
                    Add(files, SafeUpdatePath.Resolve(directory, "packages/" + package.Id), package.Size, package.Sha256);
                if (!keepVersions.Contains(numeric) && Directory.Exists(Path.Combine(directory, "staging", numeric.ToString())))
                    foreach (var file in manifest.Files)
                        Add(files, SafeUpdatePath.Resolve(directory, $"staging/{numeric}/{file.Key}"), file.Size, file.Sha256);
            }
            foreach (var worker in manifest.Files.Where(file => IsUpdater(file) && !keepWorkers.Contains(file.Sha256)))
                Add(files, SafeUpdatePath.Resolve(_stateDirectory, $"workers/{worker.Sha256}/LOPATA.Updater.exe"), worker.Size, worker.Sha256);
        }
        void Add(List<UpdateStorageFile> files, string path, long size, string sha256)
        {
            if (File.Exists(path) && new FileInfo(path).Length == size && claimed.Add(path))
                files.Add(new(path, size, sha256));
        }
    }

    private static bool IsUpdater(UpdateFile file) => file.Root == "app" && file.Path == "Updater/LOPATA.Updater.exe";

    private static long DirectorySize(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        if (!Directory.Exists(path)) return 0;
        return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }).Sum(file => file.Length);
    }
}
