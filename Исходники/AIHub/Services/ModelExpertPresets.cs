using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services;

public sealed record ModelExpertPreset([property: JsonRequired] string Name, [property: JsonRequired] MusicExpertSettings Settings)
{
    [JsonRequired]
    public string Format { get; init; } = "LOPATA.ModelPreset";
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string Model { get; init; } = MusicExpertCatalog.Model;
    [JsonRequired]
    public int ContractVersion { get; init; } = MusicExpertCatalog.ContractVersion;
    public string Collection { get; init; } = "Expert";
    public ModelPresetRecipe? Recipe { get; init; }
}

/// <summary>Model-aware, bounded JSON exchange. Imports never resolve code, paths or dependencies.</summary>
public sealed class ModelExpertPresets(string directory, string collection = "Expert", string variation = MusicComponentCatalog.ModelId)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static ModelExpertPresets Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Expert", MusicExpertCatalog.Model));
    public static ModelExpertPresets Simple { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Simple", MusicExpertCatalog.Model), "Simple");
    public static ModelExpertPresets For(string variation, string collection = "Expert") => variation == MusicComponentCatalog.ModelId
        ? collection == "Simple" ? Simple : Default
        : new(Path.Combine(AppDataPaths.BaseDirectory, "Music", collection, variation), collection, variation);
    public string Collection => collection;
    private string Library => Path.Combine(directory, "presets.json");
    public IReadOnlyList<ModelExpertPreset> Load()
    {
        if (!File.Exists(Library)) return [];
        var presets = Read<ModelExpertPreset[]>(Library);
        foreach (var preset in presets) { Validate(preset); CheckCollection(preset); }
        if (presets.Length > 200 || presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != presets.Length)
            throw new InvalidDataException("Duplicate names or too many presets.");
        return presets;
    }
    public void Save(IEnumerable<ModelExpertPreset> presets)
    {
        var list = presets.ToArray(); foreach (var p in list) { Validate(p); CheckCollection(p); }
        if (list.Length > 200 || list.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
            throw new InvalidDataException("Duplicate names or too many presets.");
        Write(Library, list);
    }
    public MusicExpertSettings Current()
    {
        var path = Path.Combine(directory, "current.json");
        if (!File.Exists(path)) return MusicModelVariants.Defaults(variation);
        var preset = Read<ModelExpertPreset>(path); Validate(preset);
        if (preset.Settings.Variation != variation) throw new InvalidDataException("Preset belongs to another variation.");
        return preset.Settings.Snapshot();
    }
    public void SetCurrent(MusicExpertSettings settings)
    { settings.Validate(); if (settings.Variation != variation) throw new InvalidDataException("Settings variation mismatch."); Write(Path.Combine(directory, "current.json"), Create("Current", settings, "Expert")); }
    public static ModelExpertPreset Create(string name, MusicExpertSettings settings, string kind) =>
        new(name, settings.Snapshot()) { SchemaVersion = SchemaFor(settings.Variation), Collection = kind, Model = MusicModelVariants.Family(settings.Variation),
            Recipe = MusicTuningRecipes.For(settings.Variation).FirstOrDefault(r => r.Id == settings.Tuning?.SimplePreset)?.Metadata };
    public static ModelExpertPreset Import(string path)
    { var result = Read<ModelExpertPreset>(path); Validate(result); return result with { Settings = result.Settings.Snapshot() }; }
    public static void Export(string path, ModelExpertPreset preset)
    { Validate(preset); Write(path, preset); }
    public static string ExportName(string name, DateTime date, string variation = MusicComponentCatalog.ModelId) => "LOPATA_Preset_" +
        string.Concat(name.Take(100).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.') +
        "_" + (MusicDiffRhythmCatalog.IsDiff(variation) ? "DiffRhythm2" : MusicAceCatalog.IsAce(variation) ? "ACE15_XL_Turbo_4B" : MusicExpertCatalog.Model + (variation == MusicModelVariants.Bf16 ? "_BF16" : variation == MusicStudioRuntime.Variation ? "_Studio_Q8" : "")) + "_" + date.ToString("yyyy-MM-dd") + ".json";
    public static int SchemaFor(string variation) => MusicDiffRhythmCatalog.IsDiff(variation) ? 7 : MusicAceCatalog.IsAce(variation) ? 6 : variation == MusicComponentCatalog.ModelId ? 2 : 3;
    public static void Validate(ModelExpertPreset preset)
    {
        if (preset.Format != "LOPATA.ModelPreset" || preset.SchemaVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7) ||
            MusicDiffRhythmCatalog.IsDiff(preset.Settings?.Variation ?? "") != (preset.SchemaVersion == 7) ||
            MusicAceCatalog.IsAce(preset.Settings?.Variation ?? "") != (preset.SchemaVersion is 4 or 5 or 6) ||
            preset.Settings?.Variation is MusicModelVariants.Bf16 or MusicStudioRuntime.Variation && preset.SchemaVersion != 3)
            throw new InvalidDataException("Unsupported preset file/schema.");
        if (preset.SchemaVersion is 4 or 5 && preset.Recipe is not null || preset.SchemaVersion == 4 && preset.Settings?.Tuning is not null)
            throw new InvalidDataException("Unsupported ACE preset metadata.");
        if (preset.Collection is not ("Expert" or "Simple") || preset.SchemaVersion == 1 &&
            (preset.Collection != "Expert" || preset.Recipe is not null || preset.Settings?.Tuning is not null))
            throw new InvalidDataException("Unsupported preset collection/metadata.");
        if (preset.Model != (MusicModelVariants.Family(preset.Settings?.Variation ?? "")) || preset.ContractVersion != MusicExpertCatalog.ContractVersion)
            throw new InvalidDataException("Preset belongs to another model or parameter contract: " + preset.Model);
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 100 || preset.Name.Any(char.IsControl) || preset.Settings is null)
            throw new InvalidDataException("Invalid preset name/settings.");
        preset.Settings.Validate();
        preset.Recipe?.Validate(preset.Settings.Variation);
    }
    public void CheckCollection(ModelExpertPreset preset)
    { if (preset.Collection != collection || preset.Settings.Variation != variation) throw new InvalidDataException("Preset collection or model variation mismatch."); }
    private static T Read<T>(string path)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Preset file exceeds 1 MB.");
        using var input = File.OpenRead(path);
        using var document = JsonDocument.Parse(input, new() { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Empty preset file.");
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON field: " + property.Name);
                RejectDuplicates(property.Value);
            }
        } else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
    private static void Write<T>(string path, T value)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            File.Move(temporary, full, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
