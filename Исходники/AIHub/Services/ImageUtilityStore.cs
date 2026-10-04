using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageUtilityStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string CacheDirectory => Path.Combine(DirectoryPath, "cache");

    public ImageUtilityStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "jobs"));
    }

    public ImageUtilityPreferences LoadPreferences()
    {
        lock (Sync) return Read<ImageUtilityPreferences>(Path.Combine(DirectoryPath, "preferences.json")) ?? new();
    }

    public void SavePreferences(ImageUtilityPreferences preferences)
    {
        lock (Sync)
        {
            var current = LoadPreferences();
            preferences.LastProcessNumber = Math.Max(preferences.LastProcessNumber, current.LastProcessNumber);
            Write(Path.Combine(DirectoryPath, "preferences.json"), preferences);
        }
    }

    public long ReserveProcessNumber()
    {
        lock (Sync)
        {
            var preferences = LoadPreferences();
            preferences.LastProcessNumber = checked(preferences.LastProcessNumber + 1);
            SavePreferences(preferences);
            return preferences.LastProcessNumber;
        }
    }

    public ImageUtilityJob? LoadJob(string id)
    {
        lock (Sync) return Read<ImageUtilityJob>(JobPath(id));
    }

    public IEnumerable<ImageUtilityJob> GetJobs()
    {
        lock (Sync) return Directory.EnumerateFiles(Path.Combine(DirectoryPath, "jobs"), "*.json")
            .Select(Read<ImageUtilityJob>).OfType<ImageUtilityJob>().OrderByDescending(x => x.StartedAt).ToArray();
    }

    public void SaveJob(ImageUtilityJob job)
    {
        lock (Sync) Write(JobPath(job.Id), job);
    }

    private string JobPath(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid job identifier.", nameof(id));
        return Path.Combine(DirectoryPath, "jobs", id + ".json");
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        // Corrupt persistence is surfaced, never silently replaced with a reset counter.
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
    }

    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
