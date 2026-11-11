using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.GameSounds;

internal enum GameSoundPlacement { Auto, Vram, Hybrid, Cpu }

internal sealed record GameSoundModelOptions(string ModelId = ManagedModelCatalog.CoreArtifactId,
    GameSoundPlacement Placement = GameSoundPlacement.Auto, int Variety = 45,
    bool EditSelectedLayer = false, bool GenerationThinking = false);

internal sealed record GameSoundMessage(string Kind, string Text, string Detail = "");
internal sealed record GameSoundRequest(string Id, string Text, bool Generate, bool Orchestrate,
    GameSoundRecipe? Target, string Language, GameSoundSource[]? Layers = null);

internal sealed class GameSoundProject
{
    public int Schema { get; init; } = 1;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Number { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Name { get; set; } = "";
    public string Draft { get; set; } = "";
    public string OutputName { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public MusicAudioFormat OutputFormat { get; set; } = MusicAudioFormat.Wav;
    [JsonPropertyName("exportRecipeWithWave")] public bool ExportRecipeWithAudio { get; set; }
    public string SelectedId { get; set; } = "";
    public bool Orchestrate { get; set; }
    public bool Generate { get; set; } = true;
    public bool Loop { get; set; }
    public GameSoundModelOptions Options { get; set; } = new();
    public List<GameSoundRecipe> Recipes { get; init; } = [];
    public List<GameSoundTrack> Tracks { get; init; } = [];
    public List<GameSoundMessage> Messages { get; init; } = [];
    public List<string> Files { get; init; } = [];
    public GameSoundRequest? Pending { get; set; }
    public string LastCompletedRequest { get; set; } = "";
    [JsonIgnore] public bool Persistent { get; set; }
    [JsonIgnore] public GameSoundRecipe? Selected => Recipes.FirstOrDefault(r => r.Id == SelectedId);

    internal IReadOnlyList<GameSoundSource> Sources(bool mix)
    {
        if (!mix) return Selected is { } selected ? [new(selected)] : [];
        var sources = Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        return Tracks.Select(track => new GameSoundSource(sources[track.RecipeId], track.StartMs, track.Level, track.Enabled)).ToArray();
    }

    internal void Replace(GameSoundRecipe recipe)
    {
        var index = Recipes.FindIndex(r => r.Id == recipe.Id);
        if (index < 0) Recipes.Add(recipe);
        else Recipes[index] = recipe;
        var track = Tracks.FindIndex(t => t.RecipeId == recipe.Id);
        if (track >= 0 && Tracks[track].StartMs > GameSoundRecipes.MaxMilliseconds - recipe.Sound.DurationMs)
            Tracks[track] = Tracks[track] with { StartMs = GameSoundRecipes.MaxMilliseconds - recipe.Sound.DurationMs };
    }
}

internal sealed class GameSoundProjects(string root)
{
    internal static GameSoundProjects Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "GameSounds", "Projects"));
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object Gate = new();

    internal IReadOnlyList<GameSoundProject> List()
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<GameSoundProject>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try { result.Add(Load(id)); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("Game sound project {0}: {1}", id, error.Message); }
        }
        return result.OrderByDescending(p => p.Number).ToArray();
    }

    internal GameSoundProject Create() => new()
    {
        Number = checked((List().FirstOrDefault()?.Number ?? 0) + 1), OutputFormat = MusicAudioFormat.Opus
    };

    internal GameSoundProject Load(string id)
    {
        lock (Gate)
        {
            var project = JsonSerializer.Deserialize<GameSoundProject>(File.ReadAllBytes(PathFor(id)), Json)
                ?? throw new InvalidDataException("GameSounds.InvalidProject");
            if (project.Schema != 1 || project.Id != id || project.Options is null || project.Recipes is null ||
                project.Tracks is null || project.Messages is null || project.Files is null ||
                !Enum.IsDefined(project.Options.Placement) || project.Options.Variety is < 0 or > 100 ||
                !GameSoundAudio.Formats.Contains(project.OutputFormat))
                throw new InvalidDataException("GameSounds.InvalidProject");
            var ids = new Dictionary<string, GameSoundRecipe>();
            foreach (var recipe in project.Recipes)
            {
                GameSoundFiles.ValidateRecipe(recipe);
                if (!ids.TryAdd(recipe.Id, recipe)) throw new InvalidDataException("GameSounds.InvalidProject");
            }
            var tracks = new HashSet<string>();
            foreach (var track in project.Tracks)
            {
                if (track is null || !ids.TryGetValue(track.RecipeId, out var recipe) || !tracks.Add(track.RecipeId) ||
                    track.StartMs < 0 || track.StartMs > GameSoundRecipes.MaxMilliseconds - recipe.Sound.DurationMs ||
                    track.Level is < 0 or > 100)
                    throw new InvalidDataException("GameSounds.InvalidProject");
            }
            project.Persistent = true;
            return project;
        }
    }

    internal void Save(GameSoundProject project)
    {
        lock (Gate)
        {
            var path = PathFor(project.Id);
            Directory.CreateDirectory(root);
            GameSoundFiles.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(project, Json), CancellationToken.None);
            project.Persistent = true;
        }
    }

    private string PathFor(string id) => Guid.TryParseExact(id, "N", out _) ?
        Path.Combine(root, id + ".json") : throw new InvalidDataException("GameSounds.InvalidProject");
}
