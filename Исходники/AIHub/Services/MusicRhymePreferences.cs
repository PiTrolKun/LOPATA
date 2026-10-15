using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicRhymeOptions
{
    public string Color { get; init; } = "#36B859";
    public bool SelectionOnly { get; init; }
    public bool AutoAnalyze { get; init; } = true;
    public string ModelId { get; init; } = "core";
}

public sealed class MusicRhymePreferences(string path)
{
    public static MusicRhymePreferences Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "RhymePreferences.json"));
    public static readonly string[] Palette = ["#E85959", "#E89C3D", "#D7BE35", "#36B859", "#528EE8", "#B571DE", "#DDDDDD"];
    public static bool Allowed(string id) => id != "profile-hydra" && MusicPoetryModelCatalog.All.Any(c => c.Id == id);
    public static IReadOnlyList<MusicPoetryModelChoice> Choices(StorageSettings storage) => MusicPoetryModelCatalog.Choices(storage)
        .Where(c => Allowed(c.Candidate.Id)).ToArray();
    public MusicRhymeOptions Load()
    {
        if (!File.Exists(path)) return new();
        var options = JsonSerializer.Deserialize<MusicRhymeOptions>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Music.Rhyme.PreferencesError");
        if (!Palette.Contains(options.Color) || !Allowed(options.ModelId)) throw new InvalidDataException("Music.Rhyme.PreferencesError");
        return options;
    }
    public void Save(MusicRhymeOptions options)
    {
        if (!Palette.Contains(options.Color) || !Allowed(options.ModelId)) throw new InvalidDataException("Music.Rhyme.PreferencesError");
        RagBookPreparation.AtomicJson(path, options);
    }
}
