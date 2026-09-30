using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public AppSettings LoadOrCreate()
    {
        AppDataPaths.EnsureBaseDirectory();

        if (!File.Exists(AppDataPaths.SettingsPath))
        {
            var settings = new AppSettings();
            Save(settings);
            return settings;
        }

        try
        {
            var json = File.ReadAllText(AppDataPaths.SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.CoreVoice ??= new CoreVoiceSettings();
            settings.CoreAutonomy ??= new CoreAutonomySettings();
            settings.ModelDownloads ??= new ModelDownloadSettings();
            settings.FileViewer ??= new FileViewerSettings();
            settings.Interface ??= new InterfaceSettings();
            settings.Behavior ??= new ApplicationBehaviorSettings();
            settings.Interface.LastWindowPlacement ??= new RememberedWindowPlacement();
            settings.ImageAnalysisSpeech ??= new ImageAnalysisSpeechSettings();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppDataPaths.EnsureBaseDirectory();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AppDataPaths.SettingsPath, json);
    }
}
