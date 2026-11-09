namespace Lopata.Updates;

public sealed partial class UpdateTransaction
{
    public async Task RecoverAsync(Action<UpdateCheckpoint>? checkpoint = null, CancellationToken token = default)
    {
        using var lease = Acquire();
        var journal = ReadJournal();
        if (journal is null || journal.Phase is UpdatePhase.Healthy or UpdatePhase.RolledBack) return;
        if (journal.Phase == UpdatePhase.Prepared)
        {
            // Nothing in the installation has been touched yet.
            Save(journal with { Phase = UpdatePhase.RolledBack });
            return;
        }
        journal = journal with { Phase = UpdatePhase.RollingBack };
        Save(journal);
        var completed = 0;
        foreach (var op in journal.Operations.Reverse().Where(o => o.Action != UpdateAction.Keep))
        {
            token.ThrowIfCancellationRequested();
            var destination = _roots.Resolve(op.File);
            if (Directory.Exists(destination)) throw new IOException("Recovery destination is a directory: " + op.File.Key);
            if (op.Existed)
            {
                if (File.Exists(destination) && await UpdatePlanner.MatchesAsync(destination, op.Previous!, token)) continue;
                if (File.Exists(destination) && (op.Target is null || !await UpdatePlanner.MatchesAsync(destination, op.Target, token)))
                    throw new IOException("Recovery would overwrite a locally changed file: " + op.File.Key);
                var backup = TransactionFile(journal.Id, op.File, "backup");
                if (!await UpdatePlanner.MatchesAsync(backup, op.Previous!, token))
                    throw new InvalidDataException("Recovery backup checksum mismatch: " + op.File.Key);
                var temporary = TransactionFile(journal.Id, op.File, "restore-" + Guid.NewGuid().ToString("N"));
                await DurableUpdateFiles.CopyAsync(backup, temporary, token);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                SafeUpdatePath.RejectLinks(destination);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            else if (File.Exists(destination))
            {
                if (op.Target is null || !await UpdatePlanner.MatchesAsync(destination, op.Target, token))
                    throw new IOException("Recovery would remove a locally changed file: " + op.File.Key);
                File.Delete(destination);
            }
            checkpoint?.Invoke(new("file-restored", ++completed, op.File.Key));
        }
        if (journal.Previous is not null) DurableUpdateFiles.WriteJson(InstalledManifestPath, journal.Previous);
        else if (File.Exists(InstalledManifestPath))
        {
            CheckInstalledManifest(journal.Target);
            File.Delete(InstalledManifestPath);
        }
        Save(journal with { Phase = UpdatePhase.RolledBack });
        checkpoint?.Invoke(new("rolled-back", completed, null));
    }

    /// <summary>Called only by the full installer after all files have been installed and verified.</summary>
    public async Task RegisterInstalledAsync(SignedManifest installed, CancellationToken token = default)
    {
        using var lease = Acquire();
        if (ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            throw new IOException("An unfinished file update requires recovery before registration.");
        var manifest = installed.Verify(_keys);
        foreach (var file in manifest.Files)
            if (!await UpdatePlanner.MatchesAsync(_roots.Resolve(file), file, token))
                throw new InvalidDataException("Full installer files do not match manifest: " + file.Key);
        if (File.Exists(InstalledManifestPath))
            RememberCollectionOwnership(SignedManifest.Read(await File.ReadAllBytesAsync(InstalledManifestPath, token)));
        DurableUpdateFiles.WriteJson(InstalledManifestPath, installed);
    }

    public async Task RemoveManagedFilesAsync(CancellationToken token = default)
    {
        using var lease = Acquire();
        if (ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            throw new IOException("Recover the unfinished update before uninstalling.");
        SafeUpdatePath.RejectLinks(InstalledManifestPath);
        var owned = ReadCollectionOwnership();
        if (File.Exists(InstalledManifestPath))
        {
            var signed = SignedManifest.Read(await File.ReadAllBytesAsync(InstalledManifestPath, token));
            var manifest = signed.Verify(_keys);
            RememberCollectionOwnership(signed);
            foreach (var file in manifest.Files) owned[file.Key] = file;
        }
        var retained = new List<object>();
        foreach (var file in owned.Values)
        {
            var path = _roots.Resolve(file);
            try
            {
                if (!File.Exists(path)) continue;
                if (await UpdatePlanner.MatchesAsync(path, file, token)) File.Delete(path);
                else if (CollectionPayloadRetention.IsVersioned(file)) retained.Add(new { file, reason = "locally-changed" });
            }
            catch (Exception error) when (CollectionPayloadRetention.IsVersioned(file)
                && error is not InvalidDataException && (error is IOException || error is UnauthorizedAccessException))
            {
                // Never restart Explorer or schedule deletion of bytes that could change before reboot.
                retained.Add(new { file, reason = error.GetType().Name });
            }
        }
        DurableUpdateFiles.WriteJson(Path.Combine(_stateDirectory, "collection-retained.json"), retained);
        File.Delete(InstalledManifestPath);
        // Without an installation there is no transaction to recover. Cached packages/backups remain expendable.
        SafeUpdatePath.RejectLinks(JournalPath);
        File.Delete(JournalPath);
    }
}
