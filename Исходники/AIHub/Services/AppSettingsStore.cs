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
            settings.ImageGeneration ??= new();
            settings.ScreenCapture ??= new();
            settings.ScreenCapture.Normalize();
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
        var temporary = AppDataPaths.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temporary, AppDataPaths.SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
