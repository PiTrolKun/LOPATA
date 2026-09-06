using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class PromptPairStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private string? _loadedHash;
    private bool _loaded;

    public static PromptPairStore ForUser() => new(
        Path.Combine(AppDataPaths.BaseDirectory, "Prompts", "pairs.json"));

    public List<PromptPairPreset> Load()
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        var document = bytes.Length == 0 && !File.Exists(path)
            ? new Document() : JsonSerializer.Deserialize<Document>(bytes, Options)
                ?? throw new InvalidDataException("PromptPairs.InvalidData");
        if (document.SchemaVersion != 1 || document.Items is null)
            throw new InvalidDataException("PromptPairs.InvalidData");
        Validate(document.Items);
        _loadedHash = Hash(bytes);
        _loaded = true;
        return document.Items.ToList();
    }

    public void Save(IReadOnlyList<PromptPairPreset> items)
    {
        Validate(items);
        if (!_loaded) throw new InvalidOperationException("PromptPairs.Reload");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        // Serialize writers, including separate application instances.
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (Hash(previous) != _loadedHash)
            throw new InvalidOperationException("PromptPairs.Reload");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Items = items.ToList() }, Options);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            _loadedHash = Hash(bytes);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Validate(IReadOnlyList<PromptPairPreset> items)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _)
                || string.IsNullOrWhiteSpace(item.ContractId)
                || string.IsNullOrWhiteSpace(item.Name)
                || string.IsNullOrWhiteSpace(item.AnalysisPrompt)
                || string.IsNullOrWhiteSpace(item.ComposePrompt))
                throw new InvalidDataException("PromptPairs.Required");
            if (!ids.Add(item.Id)) throw new InvalidDataException("PromptPairs.InvalidData");
            if (!names.Add(item.ContractId + "\n" + item.Name.Trim()))
                throw new InvalidDataException("PromptPairs.Duplicate");
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Document
    {
        [System.Text.Json.Serialization.JsonRequired] public int SchemaVersion { get; set; } = 1;
        [System.Text.Json.Serialization.JsonRequired] public List<PromptPairPreset> Items { get; set; } = [];
    }
}
