using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace AIHub.Services.GameSounds;

internal static class GameSoundFiles
{
    internal const string RecipeExtension = ".lopataSfx.json";

    internal static GameSoundDocument Create(string name, IReadOnlyList<GameSoundSource> sources, bool isMix = false)
    {
        if (sources.Count == 0) throw new InvalidDataException("GameSounds.NoLayers");
        var models = sources.SelectMany(s => s.Recipe.Models).Distinct().ToArray();
        var provenance = new OutputProvenance
        {
            Scenario = "game-sounds/recipe",
            CreatedUtc = sources.Min(s => s.Recipe.CreatedUtc),
            Models = models,
            ModelHistory = models.Length == 0 ? "not-applicable" : "recorded"
        };
        return new(1, GameSoundSynthesizer.Revision, GameSoundSynthesizer.SampleRate, name, sources.ToArray(), provenance, isMix);
    }

    internal static GameSoundDocument Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 128L * 1024 * 1024) throw new InvalidDataException("GameSounds.InvalidRecipe");
        var document = JsonSerializer.Deserialize<GameSoundDocument>(input, GameSoundProjects.Json)
            ?? throw new InvalidDataException("GameSounds.InvalidRecipe");
        if (document.Schema != 1 || document.Synthesizer != GameSoundSynthesizer.Revision ||
            document.SampleRate != GameSoundSynthesizer.SampleRate)
            throw new InvalidDataException("GameSounds.UnsupportedRecipe");
        if (document.Layers is not { Length: > 0 } || document.Provenance is null)
            throw new InvalidDataException("GameSounds.InvalidRecipe");
        foreach (var layer in document.Layers)
        {
            if (layer is null) throw new InvalidDataException("GameSounds.InvalidRecipe");
            ValidateRecipe(layer.Recipe);
            if (layer.StartMs < 0 || layer.StartMs > GameSoundRecipes.MaxMilliseconds - layer.Recipe.Sound.DurationMs ||
                layer.Level is < 0 or > 100)
                throw new InvalidDataException("GameSounds.InvalidRecipe");
        }
        return document;
    }

    internal static void ValidateRecipe(GameSoundRecipe? recipe)
    {
        if (recipe is null || !Guid.TryParseExact(recipe.Id, "N", out _) || recipe.Models is null)
            throw new InvalidDataException("GameSounds.InvalidRecipe");
        GameSoundRecipes.ValidateName(recipe.Name);
        GameSoundRecipes.Validate(recipe.Sound);
        _ = new OutputProvenance { Models = recipe.Models }.Json();
    }

    internal static void SaveRecipe(string path, GameSoundDocument document, CancellationToken token)
    {
        _ = document.Provenance.Json();
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(document, GameSoundProjects.Json), token);
    }

    internal static string AudioScenario(MusicAudioFormat format) =>
        "game-sounds/" + MusicOutputSettings.Extension(format)[1..];

    internal static async Task SaveAudioAsync(string path, GameSoundDocument document, MusicAudioFormat format, CancellationToken token)
    {
        if (GameSoundAudio.FormatFromPath(path) != format)
            throw new InvalidDataException("GameSounds.InvalidAudio");
        var pcm = GameSoundSynthesizer.Render(document.Layers, token);
        if (pcm.Length == 0) throw new InvalidDataException("GameSounds.NoLayers");
        var bytes = GameSoundSynthesizer.ToBytes(pcm);
        var temp = Temporary(path);
        var source = format == MusicAudioFormat.Wav ? temp : temp + ".wav";
        try
        {
            using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new WaveFileWriter(file, GameSoundSynthesizer.Format))
                writer.Write(bytes, 0, bytes.Length);
            var audibleLayers = document.Layers.Where(s => s.Enabled && s.Level > 0 && s.Recipe.Sound.LevelPercent > 0).ToArray();
            var models = audibleLayers.SelectMany(s => s.Recipe.Models).Distinct().ToArray();
            var facts = document.Provenance with { Scenario = AudioScenario(format), Models = models,
                ModelHistory = models.Length == 0 ? "not-applicable" : "recorded" };
            var tags = new Dictionary<string, string>
            {
                ["title"] = document.Name,
                ["encoded_by"] = "LOPATA",
                ["LOPATA_PROVENANCE"] = facts.Json(),
                ["LOPATA_SYNTHESIZER"] = document.Synthesizer,
                ["LOPATA_ENCODING"] = GameSoundAudio.EncodingDescription(format),
                ["LOPATA_GAME_SOUND"] = JsonSerializer.Serialize(new
                {
                    Schema = 1, Kind = document.IsMix ? "mix" : "effect", AudibleLayers = audibleLayers.Length,
                    SourceSampleRate = GameSoundSynthesizer.SampleRate, SourceSamples = pcm.Length
                })
            };
            if (format == MusicAudioFormat.Wav)
                await AudioMetadataWriter.AttachAsync(temp, format, tags, [], token);
            else
                await GameSoundAudio.EncodeAsync(source, temp, format, bytes.Length, tags, token);
            using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (source != temp && File.Exists(source)) File.Delete(source);
        }
    }

    internal static void WriteAtomic(string path, byte[] bytes, CancellationToken token)
    {
        var temp = Temporary(path);
        try
        {
            token.ThrowIfCancellationRequested();
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string Temporary(string path) => path + "." + Guid.NewGuid().ToString("N") + ".tmp";
}
