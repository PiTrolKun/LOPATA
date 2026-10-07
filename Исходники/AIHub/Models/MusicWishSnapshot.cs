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
}
