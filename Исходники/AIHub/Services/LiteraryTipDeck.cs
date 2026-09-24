using System.IO;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Rotates stable tip IDs without repeats until the current catalog has been shown.</summary>
public sealed class LiteraryTipDeck
{
    private readonly Dictionary<string, string> _tips;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _roundCategories = new(StringComparer.Ordinal);
    private readonly string? _path;
    private string? _lastId;

    public LiteraryTipDeck(IEnumerable<(string Id, string Category)> tips, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(tips);
        _tips = tips.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .DistinctBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(t => t.Id, t => t.Category ?? "", StringComparer.Ordinal);
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        Load();
    }

    /// <summary>
    /// Excluded IDs remain available later in the same cycle. Returns null when all remaining
    /// IDs are excluded; callers can retry after a visible or held tip leaves the screen.
    /// </summary>
    public string? Next(IReadOnlyCollection<string> excludedIds)
    {
        ArgumentNullException.ThrowIfNull(excludedIds);
        var excluded = new HashSet<string>(excludedIds, StringComparer.Ordinal);
        var newCycle = _seen.Count == _tips.Count;
        var candidates = _tips.Where(t => (newCycle || !_seen.Contains(t.Key)) && !excluded.Contains(t.Key)).ToList();
        if (candidates.Count == 0) return null;
        if (newCycle && candidates.Count > 1) candidates.RemoveAll(t => t.Key == _lastId);

        var groups = candidates.GroupBy(t => t.Value, StringComparer.Ordinal).ToArray();
        var unvisited = groups.Where(g => !_roundCategories.Contains(g.Key)).ToArray();
        var newRound = newCycle || unvisited.Length == 0;
        if (!newRound) groups = unvisited;
        if (_lastId is not null && _tips.TryGetValue(_lastId, out var lastCategory))
        {
            var alternatives = groups.Where(g => g.Key != lastCategory).ToArray();
            if (alternatives.Length > 0) groups = alternatives;
        }
        // Each thematic round visits every available category once, even when their sizes differ.
        var group = groups[Random.Shared.Next(groups.Length)].ToArray();
        var next = group[Random.Shared.Next(group.Length)].Key;

        if (newCycle) _seen.Clear();
        if (newRound) _roundCategories.Clear();
        _roundCategories.Add(_tips[next]);
        _seen.Add(next);
        _lastId = next;
        Save();
        return next;
    }

    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var history = JsonSerializer.Deserialize<History>(LiteraryChapterFiles.Read(_path));
            if (history?.Version != 1 || history.SeenIds is null) return;
            _seen.UnionWith(history.SeenIds.Where(id => id is not null && _tips.ContainsKey(id)));
            if (history.LastId is not null && _tips.ContainsKey(history.LastId)) _lastId = history.LastId;
            if (history.RoundCategories is not null)
                _roundCategories.UnionWith(history.RoundCategories.Where(category => category is not null && _tips.ContainsValue(category)));
        }
        catch (Exception ex) when (IsOptionalHistoryFailure(ex))
        {
            // Decorative tips must keep working when their optional history cannot be read.
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var path = Path.GetFullPath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LiteraryChapterFiles.Write(path, JsonSerializer.Serialize(new History(1, _seen.ToArray(), _lastId, _roundCategories.ToArray())));
        }
        catch (Exception ex) when (IsOptionalHistoryFailure(ex))
        {
            // The in-memory cycle still advances if this cosmetic history is not writable.
        }
    }

    private static bool IsOptionalHistoryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private sealed record History(int Version, string[]? SeenIds, string? LastId, string[]? RoundCategories = null);
}
