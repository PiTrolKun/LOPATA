namespace AIHub.Models;

public sealed record MusicPerformer(string Id, string Name, string Voice, string Range,
    string Language, IReadOnlyList<string> Timbres, IReadOnlyList<string> Delivery, string Notes)
{
    public MusicPerformer Copy() => this with { Timbres = Timbres.ToArray(), Delivery = Delivery.ToArray() };
}

/// <summary>Session-owned musical wishes. Performer IDs survive renaming for future lyric assignments.</summary>
public sealed class MusicPreferences
{
    public bool Instrumental { get; set; }
    public bool NoChoir { get; set; }
    public bool NoBacking { get; set; }
    public Dictionary<string, List<string>> Selections { get; } = new(StringComparer.Ordinal);
    public List<MusicPerformer> Performers { get; } = [];
    public IReadOnlyList<string> Selected(string category) => Selections.TryGetValue(category, out var values) ? values : [];
    public void Select(string category, IEnumerable<string> values) =>
        Selections[category] = values.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public MusicPreferences Copy()
    {
        var result = new MusicPreferences { Instrumental = Instrumental, NoChoir = NoChoir, NoBacking = NoBacking };
        foreach (var pair in Selections) result.Select(pair.Key, pair.Value);
        result.Performers.AddRange(Performers.Select(x => x.Copy())); return result;
    }
    public static bool ValidPerformerName(string name, IEnumerable<MusicPerformer> others, string? ownId = null) =>
        !string.IsNullOrWhiteSpace(name) && !others.Any(x => x.Id != ownId &&
            string.Equals(x.Name.Trim().Normalize(), name.Trim().Normalize(), StringComparison.OrdinalIgnoreCase));
}
