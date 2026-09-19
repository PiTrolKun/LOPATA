using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Independent library; image presets and legacy action pairs are never rewritten here.</summary>
public sealed class LiteraryPromptSetStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private string? _loadedHash;
    public static LiteraryPromptSetStore ForUser() => new(Path.Combine(AppDataPaths.BaseDirectory, "Prompts", "literary-sets.json"));

    public List<LiteraryPromptSet> Load()
    {
        var exists = File.Exists(path);
        var bytes = exists ? File.ReadAllBytes(path) : [];
        var document = !exists ? new Document() : JsonSerializer.Deserialize<Document>(bytes, Options)
            ?? throw new InvalidDataException("PromptPairs.InvalidData");
        if (document.SchemaVersion != 1 || document.Items is null) throw new InvalidDataException("PromptPairs.InvalidData");
        LiteraryPromptSets.Validate(document.Items);
        _loadedHash = Hash(bytes);
        return document.Items.Select(p => p.Copy()).ToList();
    }

    public void Save(IReadOnlyList<LiteraryPromptSet> items)
    {
        LiteraryPromptSets.Validate(items);
        if (_loadedHash is null) throw new InvalidOperationException("PromptPairs.Reload");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (Hash(previous) != _loadedHash) throw new InvalidOperationException("PromptPairs.Reload");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document { Items = items.ToList() }, Options);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
            _loadedHash = Hash(bytes);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class Document
    {
        [System.Text.Json.Serialization.JsonRequired] public int SchemaVersion { get; set; } = 1;
        [System.Text.Json.Serialization.JsonRequired] public List<LiteraryPromptSet> Items { get; set; } = [];
    }
}
