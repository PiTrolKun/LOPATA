using System.IO.Compression;
using System.Security.Cryptography;

namespace Lopata.Updates;

public static class UpdatePackageExtractor
{
    // Never use ExtractToDirectory for downloaded packages. Only signed entries may be written.
    public static async Task ExtractAsync(string archivePath, UpdatePackage package, UpdateManifest manifest,
        string stagingRoot, CancellationToken token = default)
    {
        manifest.Validate();
        if (!manifest.Packages.Contains(package)) throw new InvalidDataException("Package is not in manifest.");
        SafeUpdatePath.RejectLinks(archivePath);
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != package.Size
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != package.Sha256)
            throw new InvalidDataException("Update package checksum mismatch.");
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var expected = manifest.Files.Where(f => f.Package == package.Id)
            .ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (archive.Entries.Count != expected.Count) throw new InvalidDataException("Unexpected package entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Validate the complete directory before extracting even the first entry.
        foreach (var entry in archive.Entries)
        {
            if (!expected.TryGetValue(entry.FullName, out var file) || !seen.Add(entry.FullName)
                || entry.Length != file.Size || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe or unexpected package entry.");
            _ = SafeUpdatePath.Resolve(stagingRoot, entry.FullName);
        }
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var file = expected[entry.FullName];
            var destination = SafeUpdatePath.Resolve(stagingRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // A cancelled extraction is expendable; only the validated final file is consumed.
            var partial = destination + "." + Guid.NewGuid().ToString("N") + ".extracting";
            SafeUpdatePath.RejectLinks(partial);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous))
            await using (var input = entry.Open())
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += count;
                    if (copied > file.Size) throw new InvalidDataException("Package entry exceeded declared length.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (copied != file.Size) throw new InvalidDataException("Truncated package entry.");
                output.Flush(true);
            }
            if (!await UpdatePlanner.MatchesAsync(partial, file, token))
                throw new InvalidDataException("Extracted file checksum mismatch.");
            SafeUpdatePath.RejectLinks(destination);
            File.Move(partial, destination, true);
        }
    }
}
