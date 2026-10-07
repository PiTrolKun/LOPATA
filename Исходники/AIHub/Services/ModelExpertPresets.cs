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
}

/// <summary>Model-aware, bounded JSON exchange. Imports never resolve code, paths or dependencies.</summary>
public sealed class ModelExpertPresets(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static ModelExpertPresets Default { get; } = new(Path.Combine(AppDataPaths.BaseDirectory, "Music", "Expert", MusicExpertCatalog.Model));
    private string Library => Path.Combine(directory, "presets.json");
    public IReadOnlyList<ModelExpertPreset> Load()
    {
        if (!File.Exists(Library)) return [];
        var presets = Read<ModelExpertPreset[]>(Library);
        foreach (var preset in presets) Validate(preset);
        if (presets.Length > 200 || presets.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != presets.Length)
            throw new InvalidDataException("Duplicate names or too many presets.");
        return presets;
    }
    public void Save(IEnumerable<ModelExpertPreset> presets)
    {
        var list = presets.ToArray(); foreach (var p in list) Validate(p);
        if (list.Length > 200 || list.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Length)
            throw new InvalidDataException("Duplicate names or too many presets.");
        Write(Library, list);
    }
    public MusicExpertSettings Current()
    {
        var path = Path.Combine(directory, "current.json");
        if (!File.Exists(path)) return new();
        var preset = Read<ModelExpertPreset>(path); Validate(preset); return preset.Settings.Snapshot();
    }
    public void SetCurrent(MusicExpertSettings settings)
    { settings.Validate(); Write(Path.Combine(directory, "current.json"), new ModelExpertPreset("Current", settings.Snapshot())); }
    public static ModelExpertPreset Import(string path)
    { var result = Read<ModelExpertPreset>(path); Validate(result); return result with { Settings = result.Settings.Snapshot() }; }
    public static void Export(string path, ModelExpertPreset preset)
    { Validate(preset); Write(path, preset); }
    public static string ExportName(string name, DateTime date) => "LOPATA_Preset_" +
        string.Concat(name.Take(100).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd(' ', '.') +
        "_" + MusicExpertCatalog.Model + "_" + date.ToString("yyyy-MM-dd") + ".json";
    public static void Validate(ModelExpertPreset preset)
    {
        if (preset.Format != "LOPATA.ModelPreset" || preset.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported preset file/schema.");
        if (preset.Model != MusicExpertCatalog.Model || preset.ContractVersion != MusicExpertCatalog.ContractVersion)
            throw new InvalidDataException("Preset belongs to another model or parameter contract: " + preset.Model);
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 100 || preset.Name.Any(char.IsControl) || preset.Settings is null)
            throw new InvalidDataException("Invalid preset name/settings.");
        preset.Settings.Validate();
    }
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
