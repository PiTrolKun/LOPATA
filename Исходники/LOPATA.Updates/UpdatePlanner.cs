using System.Security.Cryptography;

namespace Lopata.Updates;

public enum UpdateAction { Keep, Write, Delete }
public sealed record UpdateOperation(UpdateAction Action, UpdateFile? Previous, UpdateFile? Target, bool Existed)
{
    public UpdateFile File => Target ?? Previous ?? throw new InvalidDataException("Missing operation file.");
}
public sealed record UpdatePlan(UpdateManifest Target, UpdateOperation[] Operations, UpdatePackage[] Packages)
{
    public long DownloadBytes => Packages.Sum(p => p.Size);
    public int ChangedFiles => Operations.Count(p => p.Action != UpdateAction.Keep);
}

public static class UpdatePlanner
{
    public static async Task<UpdatePlan> CreateAsync(UpdateManifest? previous, UpdateManifest target,
        UpdateRoots roots, CancellationToken token = default)
    {
        previous?.Validate();
        target.Validate();
        if (previous is not null && UpdateManifest.NumericVersion(target.Version) < UpdateManifest.NumericVersion(previous.Version))
            throw new InvalidDataException("Automatic downgrades are not supported.");
        if (previous is not null && UpdateManifest.NumericVersion(target.Version) == UpdateManifest.NumericVersion(previous.Version)
            && !System.Text.Json.JsonSerializer.Serialize(previous, UpdateManifest.JsonOptions)
                .Equals(System.Text.Json.JsonSerializer.Serialize(target, UpdateManifest.JsonOptions), StringComparison.Ordinal))
            throw new InvalidDataException("A published version cannot change its manifest.");
        var owned = (previous?.Files ?? []).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var desired = target.Files.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var operations = new List<UpdateOperation>();
        foreach (var file in target.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = roots.Resolve(file);
            if (Directory.Exists(path)) throw new IOException("A directory occupies an update file path: " + file.Key);
            owned.TryGetValue(file.Key, out var old);
            var exists = File.Exists(path);
            if (exists && await MatchesAsync(path, file, token))
            {
                operations.Add(new(UpdateAction.Keep, old, file, true));
                continue;
            }
            if (exists && (old is null || !await MatchesAsync(path, old, token)))
                throw new IOException("An unowned or locally changed file would be overwritten: " + file.Key);
            operations.Add(new(UpdateAction.Write, old, file, exists));
        }
        foreach (var old in owned.Values.Where(f => !desired.ContainsKey(f.Key)))
        {
            token.ThrowIfCancellationRequested();
            var path = roots.Resolve(old);
            if (Directory.Exists(path)) throw new IOException("Owned file replaced with a directory: " + old.Key);
            if (!File.Exists(path)) continue;
            if (!await MatchesAsync(path, old, token))
                throw new IOException("An obsolete managed file was modified locally: " + old.Key);
            operations.Add(new(UpdateAction.Delete, old, null, true));
        }
        var needed = operations.Where(p => p.Action == UpdateAction.Write)
            .Select(p => p.Target!.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(target, operations.ToArray(), target.Packages.Where(p => needed.Contains(p.Id)).ToArray());
    }

    public static async Task<bool> MatchesAsync(string path, UpdateFile expected, CancellationToken token = default) =>
        await MatchesAsync(path, expected.Size, expected.Sha256, token);

    public static async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken token = default)
    {
        SafeUpdatePath.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != size) return false;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) == sha256;
    }
}
