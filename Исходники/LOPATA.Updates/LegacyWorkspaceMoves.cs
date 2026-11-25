using System.Security.Cryptography;
using System.Text.Json;

namespace Lopata.Updates;

/// <summary>Retains the path mapping needed to return migrated projects to an older installed release.</summary>
public static class LegacyWorkspaceMoves
{
    public const string FirstSupportedVersion = "0.9.25";
    private static readonly string[] Families = ["SongClip/Projects", "Shorts/Jobs", "SpeechAssets/Sessions"];
    private sealed record Move(string Family, string Id, string Destination);

    public static void Remember(string profile, string family, string id, string destination)
    {
        var entry = new Move(family, id, Path.GetFullPath(destination));
        _ = Resolve(profile, entry);
        DurableUpdateFiles.WriteJson(Receipt(profile, id, family), entry);
    }

    public static async Task RestoreAsync(string profile, CancellationToken token = default)
    {
        var directory = Path.Combine(profile, "StorageAddresses", "LegacyMoves");
        SafeUpdatePath.RejectLinks(directory);
        if (!Directory.Exists(directory)) return;
        var guard = Path.Combine(profile, "ModelLibrary", "model-storage-work.lock");
        SafeUpdatePath.RejectLinks(guard);
        Directory.CreateDirectory(Path.GetDirectoryName(guard)!);
        using var lease = new FileStream(guard, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var plans = new List<(string Receipt, string Address, string Source, string Destination, FileInfo[] Files)>();
        foreach (var family in Families)
        {
            var folder = Path.Combine(directory, family);
            SafeUpdatePath.RejectLinks(folder);
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                SafeUpdatePath.RejectLinks(path);
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid legacy storage receipt.");
                var entry = JsonSerializer.Deserialize<Move>(File.ReadAllBytes(path), UpdateManifest.JsonOptions)
                    ?? throw new InvalidDataException("Invalid legacy storage receipt.");
                var (source, address) = Resolve(profile, entry);
                if (entry.Family != family || entry.Id != Path.GetFileNameWithoutExtension(path))
                    throw new InvalidDataException("Invalid legacy storage receipt identity.");
                if (!File.Exists(address)) continue; // A transfer has not committed, or this move was already undone.
                if (File.ReadAllText(address) != entry.Destination || !Directory.Exists(entry.Destination))
                    throw new IOException("Migrated project is unavailable: " + entry.Destination);
                if (!Directory.Exists(source)) Directory.CreateDirectory(source);
                var files = Inventory(entry.Destination);
                plans.Add((path, address, source, entry.Destination, files));
            }
        }
        foreach (var group in plans.GroupBy(x => Path.GetPathRoot(x.Source)!, StringComparer.OrdinalIgnoreCase))
            if (new DriveInfo(group.Key).AvailableFreeSpace < group.Sum(x => x.Files.Sum(f => f.Length)))
                throw new IOException("Not enough disk space to restore workspaces for the older release: " + group.Key);

        foreach (var plan in plans)
            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                var old = SafeUpdatePath.ResolveInternal(plan.Source, Path.GetRelativePath(plan.Destination, file.FullName));
                if (!File.Exists(old)) continue;
                if (!await Equal(file.FullName, old, token))
                    throw new IOException("An older workspace contains a different file: " + old);
            }

        foreach (var plan in plans)
        {
            foreach (var file in plan.Files)
            {
                token.ThrowIfCancellationRequested();
                var old = SafeUpdatePath.ResolveInternal(plan.Source, Path.GetRelativePath(plan.Destination, file.FullName));
                if (File.Exists(old)) continue;
                var temp = old + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await DurableUpdateFiles.CopyAsync(file.FullName, temp, token);
                    if (!await Equal(file.FullName, temp, token)) throw new IOException("Could not verify legacy workspace " + old);
                    File.Move(temp, old);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            foreach (var file in Inventory(plan.Destination))
            {
                var old = SafeUpdatePath.ResolveInternal(plan.Source, Path.GetRelativePath(plan.Destination, file.FullName));
                if (!File.Exists(old) || !await Equal(file.FullName, old, token))
                    throw new IOException("Workspace changed while restoring: " + file.FullName);
            }
            File.Delete(plan.Address);
            foreach (var file in plan.Files)
            {
                try { File.Delete(file.FullName); }
                catch (IOException) { /* The verified legacy copy is already available. */ }
                catch (UnauthorizedAccessException) { }
            }
            File.Delete(plan.Receipt);
        }
    }

    private static (string Source, string Address) Resolve(string profile, Move entry)
    {
        if (!Families.Contains(entry.Family) || !Guid.TryParseExact(entry.Id, "N", out _)
            || !Path.IsPathFullyQualified(entry.Destination)) throw new InvalidDataException("Invalid legacy storage receipt.");
        var relative = entry.Family + "/" + entry.Id;
        var source = SafeUpdatePath.ResolveInternal(profile, relative);
        var address = SafeUpdatePath.ResolveInternal(profile, "StorageAddresses/" + relative + ".path");
        var destination = Path.GetFullPath(entry.Destination);
        SafeUpdatePath.RejectLinks(destination);
        if (!destination.EndsWith(Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || Overlaps(source, destination) || Overlaps(destination, source))
            throw new InvalidDataException("Invalid legacy workspace destination.");
        return (source, address);
    }

    private static bool Overlaps(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static FileInfo[] Inventory(string directory)
    {
        var files = new List<FileInfo>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }))
        {
            SafeUpdatePath.RejectLinks(info.FullName);
            if (info is FileInfo file) files.Add(file);
        }
        return [.. files];
    }

    private static string Receipt(string profile, string id, string family) =>
        SafeUpdatePath.ResolveInternal(profile, $"StorageAddresses/LegacyMoves/{family}/{id}.json");

    private static async Task<bool> Equal(string left, string right, CancellationToken token)
    {
        using var before = File.OpenRead(left);
        using var after = File.OpenRead(right);
        if (before.Length != after.Length) return false;
        var expected = await SHA256.HashDataAsync(before, token);
        var actual = await SHA256.HashDataAsync(after, token);
        return expected.SequenceEqual(actual);
    }
}
