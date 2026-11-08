using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.OfficeReader;

internal sealed class ReaderAiSettings
{
    internal const string DefaultSpeechModelId = "qwen3-17b";
    public string TextModel { get; set; } = ManagedModelCatalog.CoreArtifactId;
    public ChatSpeechSettings Speech { get; set; } = new() { Selected = DefaultSpeechModelId };
}

/// <summary>Reader-owned preferences; never writes chat or editor settings.</summary>
internal sealed class ReaderAiSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AppDataPaths.BaseDirectory, "OfficeReader", "ai-settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal ReaderAiSettings Load()
    {
        ReaderAiSettings value;
        try { value = File.Exists(_path) ? JsonSerializer.Deserialize<ReaderAiSettings>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        if (string.IsNullOrWhiteSpace(value.TextModel)) value.TextModel = ManagedModelCatalog.CoreArtifactId;
        value.Speech ??= new() { Selected = ReaderAiSettings.DefaultSpeechModelId };
        value.Speech.Profiles ??= [];
        foreach (var pair in value.Speech.Profiles.ToArray())
        {
            try { var p = pair.Value; VideoNarratorCatalog.Validate(pair.Key, p.Language, p.Rate, p.Options); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException)
            { value.Speech.Profiles.Remove(pair.Key); }
        }
        try { _ = value.Speech.Choice(); }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException)
        { value.Speech.Selected = ReaderAiSettings.DefaultSpeechModelId; }
        return value;
    }
    internal void Save(ReaderAiSettings value)
    {
        if (string.IsNullOrWhiteSpace(value.TextModel)) throw new InvalidDataException("Reader.Ai.InvalidChoice");
        _ = value.Speech.Choice();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
