using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageBatchStore(string root)
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string DirectoryFor(ImageBatchJob job) => Path.Combine(root, Key(job.Id));
    public string Results(ImageBatchJob job) => Path.Combine(DirectoryFor(job), "Results");
    public string Material(ImageBatchJob job, string id, string stage) =>
        Path.Combine(DirectoryFor(job), "Materials", Key(id) + "-" + Key(stage) + ".json");
    public static string Key(string value) => value.Length is > 0 and < 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        ? value : throw new InvalidDataException("Invalid batch key.");
    public void Save(ImageBatchJob job) => Write(Path.Combine(DirectoryFor(job), "batch.json"), job);
    public void SaveMaterial<T>(ImageBatchJob job, string id, string stage, T value) => Write(Material(job, id, stage), value);
    public T? Read<T>(ImageBatchJob job, string id, string stage)
    {
        var path = Material(job, id, stage);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
    }
    public IEnumerable<ImageBatchJob> LoadAll()
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ImageBatchJob? job = null;
            try
            {
                job = JsonSerializer.Deserialize<ImageBatchJob>(File.ReadAllText(Path.Combine(directory, "batch.json")), Json);
                if (job is null || job.Schema != 1 || Path.GetFileName(directory) != Key(job.Id)
                    || job.Items.Select(i => Key(i.Id)).Distinct().Count() != job.Items.Count) job = null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
            if (job is not null) yield return job;
        }
    }
    public static void Write<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Json));
    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
