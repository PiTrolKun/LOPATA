using System.IO;
using System.Text.Json;

namespace AIHub.Services.GameSounds;

internal sealed record GameSoundParameters
{
    public string Waveform { get; init; } = "sine";
    public int DurationMs { get; init; } = 300;
    public int StartHz { get; init; } = 600;
    public int EndHz { get; init; } = 600;
    public int AttackMs { get; init; } = 4;
    public int ReleaseMs { get; init; } = 100;
    public int NoisePercent { get; init; }
    public int LevelPercent { get; init; } = 65;
    public int Brightness { get; init; } = 80;
    public int PulseWidth { get; init; } = 35;
    public int VibratoHz { get; init; }
    public int VibratoCents { get; init; }
    public int ArpeggioSemitones { get; init; }
    public uint Seed { get; init; } = 73191;
}

internal sealed record GameSoundParameter(
    string Key, int Minimum, int Maximum, string Unit, Func<GameSoundParameters, int> Read,
    Func<GameSoundParameters, int, GameSoundParameters> Write);

internal sealed record GameSoundRecipe(string Id, string Name, GameSoundParameters Sound, DateTimeOffset CreatedUtc,
    OutputModelUse[] Models)
{
    internal static GameSoundRecipe Create(string name, GameSoundParameters sound, OutputModelUse[]? models = null) =>
        new(Guid.NewGuid().ToString("N"), name, sound, DateTimeOffset.UtcNow, models ?? []);
}

internal sealed record GameSoundTrack(string RecipeId, int StartMs = 0, int Level = 100, bool Enabled = true);
internal sealed record GameSoundSource(GameSoundRecipe Recipe, int StartMs = 0, int Level = 100, bool Enabled = true);
internal sealed record GameSoundDocument(int Schema, string Synthesizer, int SampleRate, string Name,
    GameSoundSource[] Layers, OutputProvenance Provenance, bool IsMix = false);

internal static class GameSoundRecipes
{
    internal const int MaxMilliseconds = 5000;
    internal static readonly string[] Waveforms = ["sine", "triangle", "square", "saw"];
    internal static readonly GameSoundParameter[] Parameters =
    [
        new("durationMs", 25, MaxMilliseconds, "Ms", p => p.DurationMs, (p,v) => p with { DurationMs = v }),
        new("startHz", 30, 6000, "Hz", p => p.StartHz, (p,v) => p with { StartHz = v }),
        new("endHz", 30, 6000, "Hz", p => p.EndHz, (p,v) => p with { EndHz = v }),
        new("brightness", 0, 100, "Percent", p => p.Brightness, (p,v) => p with { Brightness = v }),
        new("attackMs", 1, 1000, "Ms", p => p.AttackMs, (p,v) => p with { AttackMs = v }),
        new("releaseMs", 2, 4500, "Ms", p => p.ReleaseMs, (p,v) => p with { ReleaseMs = v }),
        new("noisePercent", 0, 100, "Percent", p => p.NoisePercent, (p,v) => p with { NoisePercent = v }),
        new("levelPercent", 0, 100, "Percent", p => p.LevelPercent, (p,v) => p with { LevelPercent = v }),
        new("pulseWidth", 5, 95, "Percent", p => p.PulseWidth, (p,v) => p with { PulseWidth = v }),
        new("vibratoHz", 0, 30, "Hz", p => p.VibratoHz, (p,v) => p with { VibratoHz = v }),
        new("vibratoCents", 0, 600, "Cents", p => p.VibratoCents, (p,v) => p with { VibratoCents = v }),
        new("arpeggioSemitones", -24, 24, "Semitones", p => p.ArpeggioSemitones, (p,v) => p with { ArpeggioSemitones = v })
    ];

    internal static readonly (string Key, GameSoundParameters Sound)[] Presets =
    [
        ("Coin", new() { Waveform = "square", DurationMs = 190, StartHz = 740, EndHz = 800, AttackMs = 2, ReleaseMs = 125, ArpeggioSemitones = 7 }),
        ("Jump", new() { Waveform = "square", DurationMs = 230, StartHz = 180, EndHz = 740, ReleaseMs = 90 }),
        ("Laser", new() { Waveform = "saw", DurationMs = 190, StartHz = 1920, EndHz = 230, AttackMs = 2, ReleaseMs = 130, NoisePercent = 8 }),
        ("Hit", new() { Waveform = "triangle", DurationMs = 125, StartHz = 250, EndHz = 65, AttackMs = 1, ReleaseMs = 92, NoisePercent = 55 }),
        ("Explosion", new() { Waveform = "sine", DurationMs = 770, StartHz = 150, EndHz = 39, AttackMs = 3, ReleaseMs = 675, NoisePercent = 72, Brightness = 53 }),
        ("Click", new() { Waveform = "sine", DurationMs = 51, StartHz = 860, EndHz = 860, AttackMs = 2, ReleaseMs = 44, NoisePercent = 5, LevelPercent = 36 })
    ];

    internal static void Validate(GameSoundParameters? sound)
    {
        if (sound is null || !Waveforms.Contains(sound.Waveform))
            throw new InvalidDataException("waveform must be sine, triangle, square or saw.");
        foreach (var parameter in Parameters)
        {
            var value = parameter.Read(sound);
            if (value < parameter.Minimum || value > parameter.Maximum)
                throw new InvalidDataException($"{parameter.Key} must be {parameter.Minimum}–{parameter.Maximum}.");
        }
        if (sound.AttackMs + sound.ReleaseMs > sound.DurationMs)
            throw new InvalidDataException("attackMs + releaseMs must fit within durationMs.");
    }

    internal static GameSoundParameters FitEnvelope(GameSoundParameters sound, string changed) => changed switch
    {
        "attackMs" => sound with { AttackMs = Math.Min(sound.AttackMs, sound.DurationMs - sound.ReleaseMs) },
        "releaseMs" => sound with { ReleaseMs = Math.Min(sound.ReleaseMs, sound.DurationMs - sound.AttackMs) },
        "durationMs" => FitDuration(sound),
        _ => sound
    };

    private static GameSoundParameters FitDuration(GameSoundParameters sound)
    {
        var attack = Math.Min(sound.AttackMs, sound.DurationMs - 2);
        return sound with { AttackMs = attack, ReleaseMs = Math.Min(sound.ReleaseMs, sound.DurationMs - attack) };
    }

    internal static object Schema(bool editing)
    {
        var properties = new Dictionary<string, object> { ["name"] = new { type = "string", minLength = 1, maxLength = 120 },
            ["waveform"] = new { type = "string", @enum = Waveforms } };
        foreach (var parameter in Parameters)
            properties.Add(parameter.Key, new { type = "integer", minimum = parameter.Minimum, maximum = parameter.Maximum });
        return new { type = "json_schema", json_schema = new { name = "game_sound_recipe", strict = true, schema = new
        {
            type = "object", additionalProperties = false, properties,
            required = editing ? [] : new[] { "name", "waveform", "durationMs", "startHz", "endHz", "attackMs",
                "releaseMs", "noisePercent", "levelPercent", "brightness" }
        } } };
    }

    internal static (string Name, GameSoundParameters Sound) Parse(string raw, GameSoundRecipe? target)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected one recipe object.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var name = target?.Name ?? "";
        var sound = target?.Sound ?? new GameSoundParameters();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!fields.Add(property.Name)) throw new InvalidDataException($"Repeated field {property.Name}.");
            if (property.Name is "name" or "waveform")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{property.Name} must be text.");
                var value = property.Value.GetString()!;
                if (property.Name == "name") name = value.Trim();
                else sound = sound with { Waveform = value };
                continue;
            }
            var parameter = Parameters.FirstOrDefault(p => p.Key == property.Name);
            if (parameter is null || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                throw new InvalidDataException($"Unexpected field or invalid integer: {property.Name}.");
            sound = parameter.Write(sound, number);
        }
        string[] required = ["name", "waveform", "durationMs", "startHz", "endHz", "attackMs", "releaseMs",
            "noisePercent", "levelPercent", "brightness"];
        if (fields.Count == 0 || target is null && required.Any(k => !fields.Contains(k)))
            throw new InvalidDataException("Return all required recipe fields when creating a sound.");
        ValidateName(name);
        Validate(sound);
        return (name, sound);
    }

    internal static void ValidateName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid recipe name.");
    }
}
