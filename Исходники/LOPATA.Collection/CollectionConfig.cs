using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

public static class CollectionPaths
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB", "Collection");
}

public sealed record CollectionConfig
{
    public int Schema { get; init; } = 1;
    public bool Enabled { get; init; }
    public string[] Roots { get; init; } = [];
    public string[] SearchRoots { get; init; } = [];
    public string[] ExcludedRoots { get; init; } = [];
    public string Language { get; init; } = "ru";
    public bool SearchEnabled { get; init; } = true;
    public bool AllowNetwork { get; init; }
    public bool AuditEnabled { get; init; } = true;
    public string Revision { get; init; } = "";
    public string ExpectedWorkerPath { get; init; } = "";

    public static CollectionConfig Load(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Collection configuration is too large.");
        var value = JsonSerializer.Deserialize<CollectionConfig>(File.ReadAllText(path), CollectionFiles.Json)
            ?? throw new InvalidDataException("Missing collection configuration.");
        value.Validate();
        return value;
    }

    public void Save(string directory)
    {
        Validate();
        Directory.CreateDirectory(directory);
        CollectionPublication.WithLock(directory, () =>
        {
            var revision = this with { Revision = Guid.NewGuid().ToString("N") };
            CollectionFiles.WriteAtomic(Path.Combine(directory, "config.json"), JsonSerializer.SerializeToUtf8Bytes(revision, CollectionFiles.Json));
            CollectionFiles.WriteAtomic(Path.Combine(directory, "enabled.bin"), BitConverter.GetBytes(Enabled ? 1U : 0U));
            CollectionFiles.WriteAtomic(Path.Combine(directory, "refresh.request"), Encoding.ASCII.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
        });
    }

    private void Validate()
    {
        if (Schema != 1 || Roots is null || SearchRoots is null || ExcludedRoots is null
            || Roots.Length > 256 || SearchRoots.Length > 256 || ExcludedRoots.Length > 256)
            throw new InvalidDataException("Unsupported collection configuration.");
        foreach (var path in Roots.Concat(SearchRoots).Concat(ExcludedRoots))
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 32700 || path.Any(char.IsControl))
                throw new InvalidDataException("Invalid collection location.");
        if (Language is not ("ru" or "en")) throw new InvalidDataException("Invalid collection language.");
        if (Revision is null || Revision.Length > 64 || Revision.Any(char.IsControl)) throw new InvalidDataException("Invalid collection revision.");
        if (ExpectedWorkerPath is null || ExpectedWorkerPath.Length > 32700 || ExpectedWorkerPath.Any(char.IsControl)
            || ExpectedWorkerPath.Length > 0 && !Path.IsPathFullyQualified(ExpectedWorkerPath))
            throw new InvalidDataException("Invalid collection worker path.");
    }
}

public static class CollectionFiles
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
