using System.IO;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Services.LiteraryImport;

public sealed class ImportPreparationAnswers
{
    private readonly string _path;
    public Dictionary<string, string> Values { get; private set; }
    public ImportPreparationAnswers(string sessionRoot)
    {
        _path = Path.Combine(sessionRoot, "preparation-answers.json");
        Values = Read(_path);
    }

    private static Dictionary<string, string> Read(string path)
    {
        if (!File.Exists(path)) return [];
        Dictionary<string, string> ReadFile(string file)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(LiteraryChapterFiles.Read(file), ImportJson.Options);
            if (values is null || values.Any(pair => pair.Value is null))
                throw new InvalidDataException("Literary.Import.PostQuestions.InvalidState");
            return values;
        }
        try { return ReadFile(path); }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            if (File.Exists(path + ".bak"))
                return ReadFile(path + ".bak");
            throw;
        }
    }

    public void Set(string key, string value)
        => SetMany(new Dictionary<string, string> { [key] = value });

    public void SetMany(IReadOnlyDictionary<string, string> changes)
    {
        if (changes.All(pair => Values.GetValueOrDefault(pair.Key) == pair.Value)) return;
        var updated = new Dictionary<string, string>(Values);
        foreach (var (key, value) in changes) updated[key] = value;
        LiteraryChapterFiles.Write(_path, JsonSerializer.Serialize(updated, ImportJson.Options));
        Values = updated;
    }
}
