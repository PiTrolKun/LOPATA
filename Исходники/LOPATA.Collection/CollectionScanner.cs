using System.Diagnostics;

namespace Lopata.Collection;

public sealed record CollectionScanResult(IReadOnlyList<CollectionItem> Items, string? Error, bool Limited, int Examined);

public sealed class CollectionScanner : IDisposable
{
    private readonly Dictionary<string, CachedOrigin> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CollectionKnownCursor _known = new();
    private readonly int _batchLimit;
    private readonly TimeSpan _timeBudget;
    private string? _knownScope;
    private sealed record CachedOrigin(long Length, long Modified, long Created, CollectionOrigin? Origin);

    public CollectionScanner(int localBatchLimit = CollectionSnapshot.MaxEntries, TimeSpan? localTimeBudget = null)
    {
        if (localBatchLimit < 1) throw new ArgumentOutOfRangeException(nameof(localBatchLimit));
        _batchLimit = localBatchLimit;
        _timeBudget = localTimeBudget ?? TimeSpan.FromSeconds(12);
    }

    public CollectionScanResult Scan(string directory, CollectionConfig config, CancellationToken token,
        Action<IReadOnlyList<CollectionItem>>? localResults = null, IReadOnlyList<CollectionItem>? previousResults = null)
    {
        var items = new Dictionary<string, CollectionItem>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examined = 0; var limited = false; var snapshotBytes = 20;
        void Add(string path, string? scenario = null, CollectionJournalEntry? journal = null)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                path = Path.GetFullPath(path);
                if (!visited.Add(path) || !CollectionFilePolicy.IsAllowed(path, config)) return;
                var file = new FileInfo(path);
                if (!file.Exists || !CollectionFilePolicy.CanRead(file)) return;
                examined++;
                CollectionOrigin? origin;
                if (_cache.TryGetValue(path, out var previous) && previous.Origin is { RequiresContentValidation: false } && previous.Length == file.Length
                    && previous.Modified == file.LastWriteTimeUtc.Ticks && previous.Created == file.CreationTimeUtc.Ticks)
                    origin = previous.Origin;
                else
                {
                    var beforeLength = file.Length;
                    var beforeModified = file.LastWriteTimeUtc.Ticks;
                    var beforeCreated = file.CreationTimeUtc.Ticks;
                    origin = CollectionOriginReader.Read(path) ?? CollectionJournal.TryResolveOrigin(path, directory);
                    file.Refresh();
                    if (!file.Exists || file.Length != beforeLength || file.LastWriteTimeUtc.Ticks != beforeModified || file.CreationTimeUtc.Ticks != beforeCreated) return;
                    if (origin is { RequiresContentValidation: false }) _cache[path] = new(beforeLength, beforeModified, beforeCreated, origin);
                    else _cache.Remove(path);
                }
                if (origin is null) return;
                var actualScenario = origin.Scenario;
                if (actualScenario.Length == 0 && journal is not null && file.Length == journal.Length
                    && file.LastWriteTimeUtc.Ticks == journal.ModifiedUtcTicks && file.CreationTimeUtc.Ticks == journal.CreatedUtcTicks)
                    actualScenario = scenario ?? "";
                var kind = CollectionLabels.Kind(path, config.Language);
                var label = CollectionLabels.Scenario(actualScenario, config.Language);
                var bytes = checked(12 + 2 * (path.Length + kind.Length + label.Length));
                if (snapshotBytes + bytes > CollectionSnapshot.MaxBytes) { limited = true; return; }
                items[path] = new(path, kind, label);
                snapshotBytes += bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }

        foreach (var entry in CollectionJournal.Read(directory))
        {
            if (items.Count >= CollectionSnapshot.MaxEntries) { limited = true; break; }
            Add(entry.Path, entry.Scenario, entry);
        }
        var scope = CollectionPublication.Revision(config);
        if (_knownScope != scope)
        {
            _knownPaths.Clear(); _known.Reset(config); _knownScope = scope;
        }
        else if (_known.Complete) _known.Reset(config);
        foreach (var path in _knownPaths.ToArray())
        {
            Add(path);
            if (!items.ContainsKey(path)) _knownPaths.Remove(path);
        }
        var timer = Stopwatch.StartNew();
        var steps = 0;
        while (steps++ < _batchLimit && timer.Elapsed <= _timeBudget)
        {
            token.ThrowIfCancellationRequested();
            if (!_known.MoveNext(config, out var path)) break;
            if (path is null) continue;
            Add(path);
            if (items.ContainsKey(path)) _knownPaths.Add(path);
            if (items.Count >= CollectionSnapshot.MaxEntries) { limited = true; break; }
        }
        limited |= !_known.Complete;
        localResults?.Invoke(Sorted(items.Values));
        string? error = null;
        if (config.SearchEnabled)
        {
            var indexed = CollectionWindowsSearch.Query(config, token);
            error = indexed.Error; limited |= indexed.Limited;
            foreach (var path in indexed.Paths)
            {
                if (items.Count >= CollectionSnapshot.MaxEntries) { limited = true; break; }
                Add(path);
            }
            if (error is not null)
            {
                // An unavailable provider must not erase a previously confirmed original.
                foreach (var previous in previousResults ?? CollectionPublication.ReadCurrent(directory, scope))
                    if (items.Count < CollectionSnapshot.MaxEntries) Add(previous.Path);
            }
        }
        if (_cache.Count > 50_000) _cache.Clear();
        return new(Sorted(items.Values), error, limited, examined);
    }

    private static IReadOnlyList<CollectionItem> Sorted(IEnumerable<CollectionItem> values) =>
        values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Dispose() => _known.Dispose();
}
