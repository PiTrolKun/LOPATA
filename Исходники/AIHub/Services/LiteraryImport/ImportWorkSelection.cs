namespace AIHub.Services.LiteraryImport;

/// <summary>Display model results without inferring new source boundaries or changing decisions.</summary>
public static class ImportWorkSelection
{
    public static IReadOnlyList<ImportPreviewGroup> Groups(ImportInput input, ImportDecision[] decisions)
    {
        var byId = decisions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var dialogs = input.Conversations.ToDictionary(c => c.Id, c => c.Title);
        var variants = input.Units.Where(u => byId.ContainsKey(u.Id))
            .GroupBy(u => byId[u.Id].Project, StringComparer.Ordinal)
            .Select(g => Variant(g.Key, g.ToArray(), dialogs)).ToArray();
        var groups = new List<List<ImportPreviewVariant>>();
        foreach (var variant in variants.Where(v => v.Name.Length > 0).OrderByDescending(v => v.Characters))
        {
            // A suggestion only; every original variant remains independently selectable.
            var group = groups.FirstOrDefault(g => g.All(v => ImportWorkNames.Similar(v.Name, variant.Name)));
            if (group is null) groups.Add([variant]); else group.Add(variant);
        }
        var result = groups.Select(g => new ImportPreviewGroup(g[0].Name, g[0].Name, false,
                g, g.Sum(v => v.Characters)))
            .OrderByDescending(g => g.Characters).ToList();
        var other = variants.SingleOrDefault(v => v.Name.Length == 0);
        if (other is not null) result.Add(new("__other__", "", true, [other], other.Characters));
        return result;
    }

    private static ImportPreviewVariant Variant(string name, ImportUnit[] units, Dictionary<string, string> dialogs)
    {
        var parts = new List<ImportPreviewPart>();
        var current = new List<ImportUnit>();
        var characters = 0;
        void Flush()
        {
            if (current.Count == 0) return;
            var first = current[0];
            var excerpt = first.Text.Trim();
            if (excerpt.Length > 220) excerpt = excerpt[..220] + "…";
            parts.Add(new(first.Id, current.Select(u => u.Id).ToArray(), first.Conversation,
                dialogs.GetValueOrDefault(first.Conversation, first.Conversation), name, excerpt, characters));
            current.Clear(); characters = 0;
        }
        foreach (var unit in units)
        {
            if (current.Count > 0 && (current[0].Message != unit.Message || current[0].Conversation != unit.Conversation
                || current.Count >= 12 || characters + unit.Text.Length > 12000)) Flush();
            current.Add(unit); characters += unit.Text.Length;
        }
        Flush();
        return new(name, parts, units.Sum(u => (long)u.Text.Length));
    }

    public static ImportDecision[] ForAssembly(ImportDecision[] first, IReadOnlySet<string> selected, string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 150 || selected.Count == 0
            || selected.Except(first.Select(d => d.Id), StringComparer.Ordinal).Any())
            throw new System.IO.InvalidDataException("Literary.Import.SelectRequired");
        return first.Select(d => selected.Contains(d.Id) ? d with { Project = title } : d).ToArray();
    }
}

public sealed record ImportAssemblySelection(string Title, string[] UnitIds);
