using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

internal static class MusicAceSource
{
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "MusicAceRuntime");
    internal static async Task VerifyAsync(CancellationToken token)
    {
        var root = DirectoryPath;
        Lopata.Updates.SafeUpdatePath.RejectLinks(root);
        var manifestPath = Path.Combine(root, "manifest.json");
        await using var input = File.OpenRead(manifestPath);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != "30A8D64E4D945F6C5E633F268554081F146FA6CE38868AE34A7001D06540E04E")
            throw new InvalidDataException("ACE source manifest integrity failure.");
        input.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync<Manifest>(input, cancellationToken: token)
            ?? throw new InvalidDataException("Missing ACE source manifest.");
        if (manifest.Revision != MusicAceCatalog.SourceRevision || manifest.Files.Count < 10)
            throw new InvalidDataException("Unsupported ACE engine revision.");
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var known = manifest.Files.Keys.Select(p => Path.GetFullPath(Path.Combine(root, p))).Append(manifestPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!known.Contains(Path.GetFullPath(path))) throw new InvalidDataException("Unexpected file in ACE source bundle: " + path);
        foreach (var (relative, expected) in manifest.Files) {
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ACE source path escapes bundle.");
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            await using var file = File.OpenRead(path);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ACE source integrity failure: " + relative);
        }
    }
    private sealed record Manifest(string Revision, Dictionary<string, string> Files);
}
