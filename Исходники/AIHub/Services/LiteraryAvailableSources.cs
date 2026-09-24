namespace AIHub.Services;

public sealed record LiteraryAvailableSource(string id, string label);
public sealed record LiteraryAvailableCatalog(LiteraryAvailableSource[] Entries, int Total);

/// <summary>Bounded directory for recommendations, never a substitute for selected source content.</summary>
public static class LiteraryAvailableSources
{
    public const int MaximumEntries = 64;
    public const int MaximumCharacters = 12000;

    public static LiteraryAvailableCatalog Build(LiteraryParagraphCatalog catalog, HashSet<string> covered)
    {
        var total = catalog.Nodes.Values.Count(n => !covered.Contains(n.Id));
        var entries = new List<LiteraryAvailableSource>();
        var queue = new Queue<ParagraphSource>();
        var characters = 2;
        bool AddLevel(IEnumerable<ParagraphSource> nodes)
        {
            var available = nodes.Where(n => !covered.Contains(n.Id)).ToArray();
            var rows = available.Select(n => new LiteraryAvailableSource(n.Id, n.Label)).ToArray();
            var size = rows.Sum(r => ParagraphJson.Encode(r).Length + 1);
            if (entries.Count + rows.Length > MaximumEntries || characters + size > MaximumCharacters) return false;
            entries.AddRange(rows); characters += size;
            foreach (var node in available) queue.Enqueue(node);
            return true;
        }
        // Ancestors stay available when their large branches do not fit. The UI retains the full tree.
        foreach (var root in catalog.Roots) AddLevel([root]);
        while (queue.TryDequeue(out var parent)) AddLevel(parent.Children);
        return new(entries.ToArray(), total);
    }
}
