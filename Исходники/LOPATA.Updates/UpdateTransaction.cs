using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lopata.Updates;

/// <summary>Caller must close the application/backends before Apply or Recover. No process is killed here.</summary>
public sealed partial class UpdateTransaction
{
    private readonly UpdateRoots _roots;
    private readonly IReadOnlyDictionary<string, string> _keys;
    private readonly string _stateDirectory;
    public string InstalledManifestPath => Path.Combine(_stateDirectory, "installed.signed.json");
    private string JournalPath => Path.Combine(_stateDirectory, "transaction.json");

    public UpdateTransaction(UpdateRoots roots, IReadOnlyDictionary<string, string> keys, string stateDirectory)
    {
        _roots = roots; _keys = keys;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        SafeUpdatePath.RejectLinks(_stateDirectory);
    }

    public UpdateJournal? ReadJournal()
    {
        SafeUpdatePath.RejectLinks(JournalPath);
        if (!File.Exists(JournalPath)) return null;
        if (new FileInfo(JournalPath).Length > 3L * SignedManifest.MaximumEnvelopeBytes)
            throw new InvalidDataException("Oversized update journal.");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(JournalPath), UpdateManifest.JsonOptions)
            ?? throw new InvalidDataException("Empty update journal.");
        ValidateJournal(journal);
        return journal;
    }

    public async Task<string> PrepareAsync(SignedManifest? previous, SignedManifest target, string stagingDirectory,
        CancellationToken token = default)
    {
        using var lease = Acquire();
        if (ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            throw new IOException("Recover the unfinished update first.");
        var before = previous?.Verify(_keys);
        var after = target.Verify(_keys);
        CheckInstalledManifest(previous);
        var plan = await UpdatePlanner.CreateAsync(before, after, _roots, token);
        var id = Guid.NewGuid().ToString("N");
        // Backup and stage every changed file before any live file can be replaced.
        foreach (var group in plan.Operations.Where(o => o.Action != UpdateAction.Keep)
            .GroupBy(o => Path.GetPathRoot(_roots.Root(o.File.Root))!, StringComparer.OrdinalIgnoreCase))
        {
            var needed = checked(group.Sum(o => (o.Target?.Size ?? 0) + (o.Existed ? o.Previous!.Size : 0)) + 16L * 1024 * 1024);
            if (new DriveInfo(group.Key).AvailableFreeSpace < needed)
                throw new IOException("Insufficient disk space for update staging and backup.");
        }
        foreach (var op in plan.Operations.Where(o => o.Action != UpdateAction.Keep))
        {
            token.ThrowIfCancellationRequested();
            if (op.Existed)
            {
                var backup = TransactionFile(id, op.File, "backup");
                await DurableUpdateFiles.CopyAsync(_roots.Resolve(op.File), backup, token);
                if (!await UpdatePlanner.MatchesAsync(backup, op.Previous!, token))
                    throw new IOException("Installed file changed during backup: " + op.File.Key);
            }
            if (op.Target is { } file)
            {
                var source = SafeUpdatePath.Resolve(stagingDirectory, file.Key);
                var destination = TransactionFile(id, file, "staged");
                await DurableUpdateFiles.CopyAsync(source, destination, token);
                if (!await UpdatePlanner.MatchesAsync(destination, file, token))
                    throw new InvalidDataException("Staged file checksum mismatch: " + file.Key);
            }
        }
        Save(new(id, UpdatePhase.Prepared, previous, target, plan.Operations));
        return id;
    }

    public async Task ApplyAsync(string id, Action<UpdateCheckpoint>? checkpoint = null, CancellationToken token = default)
    {
        using var lease = Acquire();
        var journal = Require(id, UpdatePhase.Prepared);
        CheckInstalledManifest(journal.Previous);
        await ValidateLiveBeforeApplyAsync(journal, token);
        journal = journal with { Phase = UpdatePhase.Applying };
        Save(journal);
        checkpoint?.Invoke(new("applying", 0, null));
        var completed = 0;
        foreach (var op in journal.Operations.Where(o => o.Action != UpdateAction.Keep))
        {
            token.ThrowIfCancellationRequested();
            var destination = _roots.Resolve(op.File);
            if (op.Action == UpdateAction.Delete) File.Delete(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var source = TransactionFile(id, op.File, "staged");
                if (!await UpdatePlanner.MatchesAsync(source, op.Target!, token))
                    throw new InvalidDataException("Staged file changed before replacement.");
                SafeUpdatePath.RejectLinks(destination);
                if (op.Existed) File.Replace(source, destination, null);
                else File.Move(source, destination);
            }
            // The journal describes all operations up front, so interruption here is recoverable.
            checkpoint?.Invoke(new("file-applied", ++completed, op.File.Key));
        }
        await VerifyTargetAsync(journal, token);
        DurableUpdateFiles.WriteJson(InstalledManifestPath, journal.Target);
        Save(journal with { Phase = UpdatePhase.AwaitingHealth });
        checkpoint?.Invoke(new("awaiting-health", completed, null));
    }

    public async Task ConfirmHealthyAsync(string id, CancellationToken token = default)
    {
        using var lease = Acquire();
        var journal = Require(id, UpdatePhase.AwaitingHealth);
        await VerifyTargetAsync(journal, token);
        CheckInstalledManifest(journal.Target);
        Save(journal with { Phase = UpdatePhase.Healthy });
        // Backups are retained. A later explicit retention operation may remove them.
    }

    private async Task VerifyTargetAsync(UpdateJournal journal, CancellationToken token)
    {
        foreach (var file in journal.Target.Verify(_keys).Files)
            if (!File.Exists(_roots.Resolve(file)) || !await UpdatePlanner.MatchesAsync(_roots.Resolve(file), file, token))
                throw new InvalidDataException("Installed target verification failed: " + file.Key);
    }

    private async Task ValidateLiveBeforeApplyAsync(UpdateJournal journal, CancellationToken token)
    {
        foreach (var op in journal.Operations)
        {
            var path = _roots.Resolve(op.File);
            if (op.Existed)
            {
                var expected = op.Action == UpdateAction.Keep ? op.Target! : op.Previous!;
                if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, expected, token))
                    throw new IOException("Installation changed since preparation: " + op.File.Key);
            }
            else if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("A new file occupies the update destination: " + op.File.Key);
        }
    }

    private void CheckInstalledManifest(SignedManifest? expected)
    {
        SafeUpdatePath.RejectLinks(InstalledManifestPath);
        var actual = File.Exists(InstalledManifestPath) ? SignedManifest.Read(File.ReadAllBytes(InstalledManifestPath)) : null;
        actual?.Verify(_keys);
        if (actual != expected) throw new IOException("Installed ownership manifest changed or is missing.");
    }

    private IDisposable Acquire()
    {
        SafeUpdatePath.RejectLinks(_stateDirectory);
        Directory.CreateDirectory(_stateDirectory);
        var streams = new List<FileStream>();
        try
        {
            // Root locks also cover two installations sharing a backend, or different state directories.
            var locks = _roots.RootIds.Select(id => _roots.Internal(id, ".lopata-update/update.lock"))
                .Append(Path.Combine(_stateDirectory, "update.lock")).Order(StringComparer.OrdinalIgnoreCase);
            foreach (var path in locks)
            {
                SafeUpdatePath.RejectLinks(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                streams.Add(new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            return new UpdateLease(streams);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private sealed class UpdateLease(List<FileStream> streams) : IDisposable
    {
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }

    private void Save(UpdateJournal journal) => DurableUpdateFiles.WriteJson(JournalPath, journal);
    private string TransactionFile(string id, UpdateFile file, string kind) =>
        _roots.Internal(file.Root, $".lopata-update/{id}/{kind}/{file.Path}");

    private UpdateJournal Require(string id, UpdatePhase phase)
    {
        var journal = ReadJournal() ?? throw new InvalidDataException("Missing update journal.");
        if (journal.Id != id || journal.Phase != phase) throw new InvalidDataException("Unexpected update state.");
        return journal;
    }

    private void ValidateJournal(UpdateJournal journal)
    {
        if (!Regex.IsMatch(journal.Id ?? "", "^[a-f0-9]{32}$") || !Enum.IsDefined(journal.Phase)
            || journal.Target is null || journal.Operations is null || journal.Operations.Length > 200000)
            throw new InvalidDataException("Invalid update journal.");
        var target = journal.Target.Verify(_keys).Files.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var previous = (journal.Previous?.Verify(_keys).Files ?? []).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in journal.Operations)
        {
            if (op is null || !Enum.IsDefined(op.Action) || !seen.Add(op.File.Key))
                throw new InvalidDataException("Invalid journal operation.");
            target.TryGetValue(op.File.Key, out var next);
            previous.TryGetValue(op.File.Key, out var old);
            if (op.Previous != old || op.Target != next || (op.Action == UpdateAction.Delete && (old is null || next is not null || !op.Existed))
                || (op.Action == UpdateAction.Keep && (next is null || !op.Existed))
                || (op.Action == UpdateAction.Write && (next is null || (op.Existed && old is null))))
                throw new InvalidDataException("Journal operation is not authorized by signed manifests.");
            _ = _roots.Resolve(op.File);
        }
        if (target.Keys.Any(key => !seen.Contains(key))) throw new InvalidDataException("Incomplete update journal.");
    }
}
