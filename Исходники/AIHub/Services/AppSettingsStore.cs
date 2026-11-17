using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

public sealed class AppSettingsStore
{
    private readonly CoreVoiceProfileStore _voice;
    private readonly Action<CoreVoiceSelection> _beforeMigration;

    public AppSettingsStore() : this(AppDataPaths.BaseDirectory, ApplicationUpdateStartup.RecordVoiceSettingsMigration) { }

    internal AppSettingsStore(string directory, Action<CoreVoiceSelection>? beforeMigration = null)
    {
        _voice = new(directory);
        _beforeMigration = beforeMigration ?? (_ => { });
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AppSettings LoadOrCreate()
    {
        try
        {
            using var lease = _voice.Acquire();
            if (!File.Exists(_voice.SettingsPath))
            {
                var fresh = new AppSettings();
                _voice.SavePreference(CoreVoiceSettings.KittenMiniProvider);
                SaveSettings(fresh, CoreVoiceSettings.EspeakProvider);
                return fresh;
            }

            var json = JsonNode.Parse(File.ReadAllText(_voice.SettingsPath))?.AsObject() ?? new JsonObject();
            var selection = _voice.Read(json);
            // The preference is read separately so even a malformed provider cannot block the other settings.
            if (json["coreVoice"] is JsonObject voice)
                voice["provider"] = selection.CompatibleProvider;
            var settings = json.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
            settings.ImageGeneration ??= new();
            settings.VectorGraphics ??= new();
            settings.ScreenCapture ??= new();
            settings.Dictation ??= new();
            settings.ChatSpeech ??= new();
            settings.ScreenCapture.Normalize();
            settings.CoreVoice ??= new CoreVoiceSettings();
            settings.CoreVoice.Provider = selection.Preferred;
            settings.CoreVoice.FallbackProvider = selection.CompatibleProvider;
            settings.VoiceNotifications ??= new VoiceNotificationSettings();
            settings.CoreAutonomy ??= new CoreAutonomySettings();
            settings.ModelDownloads ??= new ModelDownloadSettings();
            settings.FileViewer ??= new FileViewerSettings();
            settings.Interface ??= new InterfaceSettings();
            settings.Behavior ??= new ApplicationBehaviorSettings();
            settings.Interface.LastWindowPlacement ??= new RememberedWindowPlacement();
            settings.ImageAnalysisSpeech ??= new ImageAnalysisSpeechSettings();
            try { MigrateVoice(selection); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                OwnedProcessRegistry.Log("voice_settings_migration_failed", "settings", detail: error.Message);
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        using var lease = _voice.Acquire();
        var selection = ReadBeforeSave();
        MigrateVoice(selection);
        SaveSettings(settings, selection.CompatibleProvider);
    }

    internal void SelectCoreVoice(AppSettings settings, string provider)
    {
        using var lease = _voice.Acquire();
        var selection = ReadBeforeSave();
        MigrateVoice(selection);
        var legacy = CoreVoiceProfileStore.LegacyProvider(provider) ?? selection.CompatibleProvider;
        SaveSettings(settings, legacy);
        _voice.SavePreference(provider == CoreVoiceSettings.KittenMiniProvider ? provider : null);
        settings.CoreVoice.Provider = provider;
        settings.CoreVoice.FallbackProvider = legacy;
    }

    private void MigrateVoice(CoreVoiceSelection selection)
    {
        if (selection.LegacyProvider == selection.CompatibleProvider) return;
        _beforeMigration(selection);
        _voice.SavePreference(selection.SeparatePreference);
        _voice.SaveCompatibleProvider(selection.CompatibleProvider);
    }

    private CoreVoiceSelection ReadBeforeSave()
    {
        try { return _voice.Read(); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return _voice.Read(new JsonObject()) with { LegacyProvider = CoreVoiceSettings.EspeakProvider };
        }
    }

    private void SaveSettings(AppSettings settings, string legacy) =>
        _voice.SaveSharedSettings(JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject(), legacy);
}
