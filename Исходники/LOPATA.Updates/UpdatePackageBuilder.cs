using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Lopata.Updates;

public static class UpdatePackageBuilder
{
    // Stable buckets keep small-file changes from redownloading the complete runtime.
    // Large binaries get their own content-addressed package.
    public static async Task<UpdateManifest> BuildAsync(IReadOnlyDictionary<string, string> sourceRoots,
        string outputDirectory, string version, string sourceCommit, UpdateNote[] notes,
        UpdateManifest? reuse = null, CancellationToken token = default)
    {
        _ = UpdateManifest.NumericVersion(version);
        reuse?.Validate();
        var roots = new UpdateRoots(sourceRoots);
        var output = Path.GetFullPath(outputDirectory);
        SafeUpdatePath.RejectLinks(output);
        foreach (var source in sourceRoots.Values)
        {
            var prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((output + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package output must be outside source roots.");
        }
        Directory.CreateDirectory(output);
        var files = new List<UpdateFile>();
        foreach (var (root, _) in sourceRoots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var path in EnumerateFiles(roots.Root(root)))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(roots.Root(root), path).Replace('\\', '/');
                SafeUpdatePath.ValidateRelative(relative);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var size = stream.Length;
                var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
                var bootstrapLicense = root == "app" && (relative.StartsWith("Licenses/", StringComparison.Ordinal)
                    || relative == BootstrapPreparation.ProtocolFile);
                var bucket = bootstrapLicense ? "app-bootstrap-licenses" : size >= 8 * 1024 * 1024
                    ? "large-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root + "/" + relative)))[..16]
                    : root + "-small-" + (SHA256.HashData(Encoding.UTF8.GetBytes(relative))[0] % 32).ToString("d2");
                files.Add(new(root, relative, size, sha256, bucket));
            }
        }
        var packaged = new List<UpdateFile>();
        var packages = new List<UpdatePackage>();
        foreach (var group in files.GroupBy(f => f.Package).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var temporary = Path.Combine(output, Guid.NewGuid().ToString("N") + ".building");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    foreach (var file in group.OrderBy(f => f.Key, StringComparer.Ordinal))
                    {
                        var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var content = entry.Open();
                        await using var input = new FileStream(roots.Resolve(file), FileMode.Open, FileAccess.Read, FileShare.Read,
                            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await input.CopyToAsync(content, token);
                        input.Position = 0;
                        if (input.Length != file.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)) != file.Sha256)
                            throw new IOException("Payload changed while packaging: " + file.Key);
                    }
                stream.Flush(true);
            }
            var bytes = new FileInfo(temporary).Length;
            await using var inputZip = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(inputZip, token));
            await inputZip.DisposeAsync();
            var id = "pkg-" + hash + ".zip";
            var destination = SafeUpdatePath.Resolve(output, id);
            if (File.Exists(destination))
            {
                if (!await UpdatePlanner.MatchesAsync(destination, bytes, hash, token))
                    throw new IOException("Existing content-addressed package is corrupt.");
                File.Delete(temporary);
            }
            else File.Move(temporary, destination);
            var old = reuse?.Packages.FirstOrDefault(p => p.Id == id && p.Size == bytes && p.Sha256 == hash);
            packages.Add(old ?? new(id, $"https://github.com/PiTrolKun/LOPATA/releases/download/v{version}/{id}", bytes, hash));
            packaged.AddRange(group.Select(f => f with { Package = id }));
        }
        var manifest = new UpdateManifest
        {
            Version = version, SourceCommit = sourceCommit, Notes = notes, Packages = packages.ToArray(),
            Files = packaged.OrderBy(f => f.Key, StringComparer.Ordinal).ToArray()
        };
        manifest.Validate();
        return manifest;
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        SafeUpdatePath.RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            SafeUpdatePath.RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                foreach (var nested in EnumerateFiles(entry)) yield return nested;
            }
            else yield return entry;
        }
    }
}
