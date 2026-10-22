using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>Shared installed-component checks; input/output document hashes deliberately do not use this policy.</summary>
internal static class RuntimeFileAudit
{
    internal static string InventoryPath(string directory, string revision)
    {
        var identity = Path.GetFullPath(directory).ToUpperInvariant() + "|" + revision;
        return Path.Combine(ComponentAudit.StoreDirectory, "inventories",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    internal static async Task SaveInventoryAsync(string directory, string revision, IEnumerable<string> paths, CancellationToken token)
    {
        var destination = InventoryPath(directory, revision);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<string[]> InventoryFilesAsync(string directory, string revision, CancellationToken token)
    {
        await using var stream = File.OpenRead(InventoryPath(directory, revision));
        var files = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: token)
            ?? throw new InvalidDataException("Missing component file inventory.");
        if (files.Length is 0 or > 100000) throw new InvalidDataException("Invalid component file inventory.");
        return files.Select(relative => Contained(directory, relative)).ToArray();
    }

    internal static async Task PresenceInventoryAsync(string directory, string revision, CancellationToken token)
    {
        foreach (var path in await InventoryFilesAsync(directory, revision, token))
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Required component file is missing: " + path);
        }
    }

    internal static string Revision(string revision, IReadOnlyList<PinnedTreeFile> files) =>
        revision + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}|{f.Size}|{f.Sha256}")))));

    internal static Task VerifyAsync(string component, string directory, string revision,
        IReadOnlyList<PinnedTreeFile> files, CancellationToken token, Action<string>? log = null)
    {
        if (files.Count == 0) throw new InvalidDataException("Missing required component file list.");
        var identity = Revision(revision, files);
        return ComponentAudit.RunAsync(component, directory, identity, Presence, Full, token, log);

        Task Presence(CancellationToken ct)
        {
            foreach (var expected in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(Contained(directory, expected.Path)))
                    throw new FileNotFoundException("Required component file is missing: " + expected.Path);
            }
            return Task.CompletedTask;
        }
        async Task Full(CancellationToken ct)
        {
            foreach (var expected in files)
            {
                ct.ThrowIfCancellationRequested();
                var path = Contained(directory, expected.Path);
                await using var file = File.OpenRead(path);
                if (expected.Size >= 0 && file.Length != expected.Size
                    || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Component file checksum mismatch: " + expected.Path);
            }
        }
    }

    internal static string Contained(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Component file escapes its installation.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }
}
