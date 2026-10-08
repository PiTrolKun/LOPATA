namespace AIHub.Models;

/// <summary>Durable provenance of existing wishes; never rebuilds the saved Style during replay.</summary>
public sealed record MusicWishSnapshot(bool Instrumental, bool NoChoir, bool NoBacking,
    Dictionary<string, string[]> Selections, MusicPerformer[] Performers)
{
    public static MusicWishSnapshot Capture(MusicPreferences state) => new(state.Instrumental, state.NoChoir, state.NoBacking,
        state.Selections.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal), state.Performers.Select(p => p.Copy()).ToArray());
    public MusicWishSnapshot Snapshot() => this with {
        Selections = Selections.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
        Performers = Performers.Select(p => p.Copy()).ToArray() };
    public MusicPreferences ToPreferences()
    {
        var state = new MusicPreferences { Instrumental = Instrumental, NoChoir = NoChoir, NoBacking = NoBacking };
        foreach (var pair in Selections) state.Select(pair.Key, pair.Value);
        state.Performers.AddRange(Performers.Select(p => p.Copy())); return state;
    }
}
