using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class StorageSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public StorageSettings LoadOrCreate()
    {
        AppDataPaths.EnsureBaseDirectory();

        if (!File.Exists(AppDataPaths.StorageSettingsPath))
        {
            var settings = new StorageSettings();
            Save(settings);
            return settings;
        }

        try { return Read(); }
        catch (JsonException) { return new StorageSettings(); }
        catch (IOException) { return new StorageSettings(); }
    }

    internal StorageSettings Read()
    {
        try
        {
            var json = File.ReadAllText(AppDataPaths.StorageSettingsPath);
            return JsonSerializer.Deserialize<StorageSettings>(json, JsonOptions) ?? new StorageSettings();
        }
        catch (FileNotFoundException)
        {
            return new StorageSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new StorageSettings();
        }
    }

    public void Save(StorageSettings settings)
    {
        AppDataPaths.EnsureBaseDirectory();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var path = AppDataPaths.StorageSettingsPath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(System.Text.Encoding.UTF8.GetBytes(json));
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
