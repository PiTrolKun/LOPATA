using System.Text.Json;

namespace Lopata.Updates;

public sealed partial class UpdateTransaction
{
    public const int ProtectedRollbackVersions = 3;

    private string HistoryDirectory => Path.Combine(_stateDirectory, "rollback-history");

    private void RememberHealthyTransaction(UpdateJournal journal)
    {
        if (journal.Previous is null) return;
        var path = Path.Combine(HistoryDirectory, journal.Id + ".json");
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) DurableUpdateFiles.WriteJson(path, journal);
    }

    internal IReadOnlyList<UpdateJournal> HealthyTransactions()
    {
        SafeUpdatePath.RejectLinks(HistoryDirectory);
        var result = new List<UpdateJournal>();
        if (ReadJournal() is { Phase: UpdatePhase.Healthy, Previous: not null } latest) result.Add(latest);
        if (!Directory.Exists(HistoryDirectory)) return result;
        foreach (var path in Directory.EnumerateFiles(HistoryDirectory, "*.json"))
        {
            SafeUpdatePath.RejectLinks(path);
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _) || result.Any(x => x.Id == id)) continue;
            if (new FileInfo(path).Length > 3L * SignedManifest.MaximumEnvelopeBytes)
                throw new InvalidDataException("Oversized rollback history record.");
            var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(path), UpdateManifest.JsonOptions)
                ?? throw new InvalidDataException("Empty rollback history record.");
            ValidateJournal(journal);
            if (journal.Id != id || journal.Phase != UpdatePhase.Healthy || journal.Previous is null)
                throw new InvalidDataException("Invalid rollback history record.");
            result.Add(journal);
        }
        return result;
    }

    private IReadOnlyList<UpdateJournal> RollbackChain(int limit)
    {
        var installed = SignedManifest.Read(File.ReadAllBytes(InstalledManifestPath));
        var history = HealthyTransactions();
        var chain = new List<UpdateJournal>();
        while (chain.Count < limit)
        {
            var next = history.Where(x => x.Target == installed && !chain.Any(y => y.Id == x.Id))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(Path.Combine(HistoryDirectory, x.Id + ".json")))
                .FirstOrDefault();
            if (next is null) break;
            chain.Add(next);
            installed = next.Previous!;
        }
        return chain;
    }

    public IReadOnlyList<string> RollbackVersions()
    {
        using var lease = Acquire();
        if (ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) }) return [];
        return RollbackChain(ProtectedRollbackVersions).Select(x => x.Previous!.Verify(_keys).Version).ToArray();
    }

    public async Task RollbackToAsync(string version, CancellationToken token = default)
    {
        using var lease = Acquire();
        if (ReadJournal() is { Phase: not (UpdatePhase.Healthy or UpdatePhase.RolledBack) })
            throw new IOException("Recover the unfinished update first.");
        var chain = RollbackChain(ProtectedRollbackVersions);
        var end = chain.ToList().FindIndex(x => x.Previous!.Verify(_keys).Version == version);
        if (end < 0) throw new InvalidOperationException("The requested version is not a retained rollback point.");
        var steps = chain.Take(end + 1).ToArray();
        foreach (var journal in steps)
            foreach (var item in journal.Operations.Where(x => x.Existed && x.Action != UpdateAction.Keep))
                if (!await UpdatePlanner.MatchesAsync(TransactionFile(journal.Id, item.File, "backup"), item.Previous!, token))
                    throw new InvalidDataException("Rollback backup missing or damaged: " + item.File.Key);
        foreach (var journal in steps)
        {
            CheckInstalledManifest(journal.Target);
            await VerifyTargetAsync(journal, token);
            await RecoverLockedAsync(journal, null, token);
        }
    }
}
