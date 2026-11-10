using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;

namespace Lopata.UpdateTool;

public sealed record ReleaseInputChanges(string[] RestoredPaths, string[] RestoredTextBytes, string[] ContentChangesKept);

/// <summary>Developer-only preparation of authenticated release inputs. Installed path/ZIP rules remain strict.</summary>
public static class ReleaseInputPreparation
{
    private const long MaximumTextBytes = 32L * 1024 * 1024;

    public static async Task<ReleaseInputChanges> RestorePreviousAsync(IReadOnlyDictionary<string, string> sourceRoots,
        UpdateManifest previous, string packageDirectory, string preparationDirectory, CancellationToken token = default)
    {
        previous.Validate();
        var roots = new UpdateRoots(sourceRoots);
        RejectNestedPreparation(sourceRoots, preparationDirectory);
        var stage = SafeUpdatePath.Resolve(preparationDirectory, "previous-" + Guid.NewGuid().ToString("N"));
        var candidates = new List<(UpdateFile File, string Destination, bool Missing)>();
        foreach (var file in previous.Files.Where(IsOrdinaryFile))
        {
            if (!sourceRoots.ContainsKey(file.Root))
                throw new InvalidDataException("A previous delivery root cannot be silently removed: " + file.Root);
            var destination = roots.Resolve(file);
            if (!File.Exists(destination)) candidates.Add((file, destination, true));
            else if (IsTextContract(file) && file.Size <= MaximumTextBytes
                && new FileInfo(destination).Length <= MaximumTextBytes
                && !await UpdatePlanner.MatchesAsync(destination, file, token)) candidates.Add((file, destination, false));
        }
        var missing = new List<string>(); var text = new List<string>(); var changed = new List<string>();
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new UpdatePackageDownloader(http, SafeUpdatePath.Resolve(preparationDirectory, "package-cache"));
        foreach (var group in candidates.GroupBy(item => item.File.Package))
        {
            token.ThrowIfCancellationRequested();
            var package = previous.Packages.Single(item => item.Id == group.Key);
            var archive = SafeUpdatePath.Resolve(packageDirectory, package.Id);
            if (File.Exists(archive))
            {
                // poka-yoke: a corrupt local predecessor is never silently trusted or replaced by an unverified file.
                if (!await UpdatePlanner.MatchesAsync(archive, package.Size, package.Sha256, token))
                    throw new InvalidDataException("Previous release package is corrupt: " + package.Id);
            }
            else archive = await downloader.DownloadAsync(previous, package, token: token);
            var extracted = SafeUpdatePath.Resolve(stage, package.Id[..^4]);
            await UpdatePackageExtractor.ExtractAsync(archive, package, previous, extracted, token);
            foreach (var item in group)
            {
                var source = SafeUpdatePath.Resolve(extracted, item.File.Key);
                if (item.Missing || EquivalentText(await File.ReadAllBytesAsync(source, token),
                    await File.ReadAllBytesAsync(item.Destination, token)))
                {
                    // poka-yoke: retain earlier paths and original byte contracts before packages are signed.
                    await CopyVerifiedAsync(source, item.Destination, item.File, token);
                    (item.Missing ? missing : text).Add(item.File.Key);
                }
                else changed.Add(item.File.Key);
            }
        }
        return new(missing.ToArray(), text.ToArray(), changed.ToArray());
    }

    public static async Task<string> MaterializeLegacyAsync(UpdateManifest legacySet, string developerSource,
        string preparationDirectory, CancellationToken token = default)
    {
        legacySet.Validate();
        var files = legacySet.Files.Where(file => file.Root == "app"
            && file.Path.StartsWith("studio/native/", StringComparison.Ordinal)).ToArray();
        if (files.Length == 0) throw new InvalidDataException("The signed legacy set has no Studio files.");
        var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacySet, UpdateManifest.JsonOptions)));
        var prepared = SafeUpdatePath.Resolve(preparationDirectory, "legacy-" + identity);
        var sourceRoot = Path.GetFullPath(developerSource).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relative = file.Path["studio/native/".Length..];
            SafeUpdatePath.ValidateRelative(relative);
            var source = Path.GetFullPath(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!source.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe legacy input.");
            var destination = SafeUpdatePath.Resolve(prepared, relative);
            if (File.Exists(destination) && await UpdatePlanner.MatchesAsync(destination, file, token)) continue;
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Missing or linked legacy input: " + relative);
            // poka-yoke: developer directory junctions are read-only inputs; only signed, verified ordinary files reach the packer.
            await CopyVerifiedAsync(source, destination, file, token);
        }
        return prepared;
    }

    public static void EnsurePreviousPaths(UpdateManifest target, UpdateManifest previous)
    {
        var paths = target.Files.Select(file => file.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = previous.Files.Where(IsOrdinaryFile).FirstOrDefault(file => !paths.Contains(file.Key));
        if (missing is not null) throw new InvalidDataException("The release would remove a previous file: " + missing.Key);
    }

    private static bool IsOrdinaryFile(UpdateFile file) => !EngineSetTransition.IsLegacy(file) && !EngineSetRetention.IsVersioned(file);
    private static bool IsTextContract(UpdateFile file) => file.Root == "app"
        && (file.Path.StartsWith("Licenses/", StringComparison.Ordinal)
            || file.Path.StartsWith("Tools/", StringComparison.Ordinal)
            || file.Path.StartsWith("Assets/VideoNarratorSamples/", StringComparison.Ordinal))
        && new[] { ".txt", ".json", ".md", ".py", ".ps1", ".xml", ".yaml", ".yml" }.Contains(Path.GetExtension(file.Path).ToLowerInvariant());

    private static bool EquivalentText(byte[] before, byte[] current)
    {
        var utf8 = new UTF8Encoding(false, true);
        try { return Normalize(utf8.GetString(before)) == Normalize(utf8.GetString(current)); }
        catch (DecoderFallbackException) { return false; }
        static string Normalize(string value) => value.TrimStart('\uFEFF').Replace("\r\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static void RejectNestedPreparation(IReadOnlyDictionary<string, string> roots, string preparation)
    {
        SafeUpdatePath.RejectLinks(preparation);
        var target = Path.GetFullPath(preparation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var root in roots.Values)
            if (target.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Release preparation must be outside payload roots.");
    }

    private static async Task CopyVerifiedAsync(string source, string destination, UpdateFile expected, CancellationToken token)
    {
        SafeUpdatePath.RejectLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".preparing";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (input.Length != expected.Size) throw new InvalidDataException("Release input size differs: " + expected.Key);
                await input.CopyToAsync(output, token); output.Flush(true);
            }
            if (!await UpdatePlanner.MatchesAsync(temporary, expected, token))
                throw new InvalidDataException("Release input hash differs: " + expected.Key);
            SafeUpdatePath.RejectLinks(destination);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) { SafeUpdatePath.RejectLinks(temporary); File.Delete(temporary); } }
    }
}
