using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed record MusicPoetryPreferenceState
{
    public string? FavoriteId { get; init; }
    public Dictionary<string, string> Comments { get; init; } = [];
}

/// <summary>Personal model notes and one startup preference, independent of song sessions and prompts.</summary>
public sealed class MusicPoetryPreferences(string path)
{
    public static MusicPoetryPreferences Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory,
        "Music", "PoetryPreferences.json"));

    public MusicPoetryPreferenceState Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<MusicPoetryPreferenceState>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("Empty poetry preferences.");
            if (state.Comments is null || state.Comments.Any(pair => pair.Value is null))
                throw new InvalidDataException("Invalid poetry comments.");
            return state with { FavoriteId = MusicPoetryModelCatalog.All.Any(candidate => candidate.Id == state.FavoriteId)
                ? state.FavoriteId : null };
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid poetry preferences.", ex); }
    }

    public void SetFavorite(string? id)
    {
        if (id is not null && !MusicPoetryModelCatalog.All.Any(candidate => candidate.Id == id))
            throw new ArgumentException("Unknown songwriting model.", nameof(id));
        Save(Load() with { FavoriteId = id });
    }

    public void SetComments(IReadOnlyDictionary<string, string> comments)
    {
        var state = Load();
        foreach (var (id, text) in comments)
        {
            if (string.IsNullOrEmpty(text)) state.Comments.Remove(id);
            else state.Comments[id] = text;
        }
        Save(state);
    }

    private void Save(MusicPoetryPreferenceState state)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, state, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
