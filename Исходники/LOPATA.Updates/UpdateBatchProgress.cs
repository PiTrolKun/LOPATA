namespace Lopata.Updates;

/// <summary>A completed package cannot hide another package's active network transfer.</summary>
public sealed class UpdateBatchProgress(IEnumerable<UpdatePackage> packages)
{
    private readonly Dictionary<string, (long Bytes, string Stage)> _states = packages
        .ToDictionary(p => p.Id, _ => (0L, "connecting"), StringComparer.Ordinal);
    private readonly object _gate = new();

    public (long Bytes, string Stage) Accept(UpdateTransferProgress progress)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(progress.Package, out var previous))
                throw new InvalidDataException("Progress for an unknown update package.");
            _states[progress.Package] = (Math.Max(previous.Bytes, progress.StoredBytes), progress.Stage);
            var stage = progress.Stage;
            if (_states.Values.Any(s => s.Stage == "retrying")) stage = "retrying";
            else if (_states.Values.Any(s => s.Stage is "connecting" or "downloading")) stage = "downloading";
            return (_states.Values.Sum(s => s.Bytes), stage);
        }
    }
}
