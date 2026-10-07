using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>Verify expanded binaries and weights against the original, SHA-pinned archive.</summary>
internal static class PinnedZipExpansionVerifier
{
    internal static async Task VerifyAsync(string source, ManagedModelArtifactFile expected, string expanded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SafeUpdatePath.RejectLinks(source);
        SafeUpdatePath.RejectLinks(expanded);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != expected.SizeBytes
            || !Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native source archive differs from the pinned catalog.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > 4096) throw new InvalidDataException("Invalid native archive entry count.");
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.TrimEnd('/');
            var path = ResolveArchivePath(expanded, relative);
            if (entry.FullName.EndsWith('/')) continue;
            if (!expectedPaths.Add(path)) throw new InvalidDataException("Duplicate native archive path.");
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Native expansion exceeds its bound.");
            await using var actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (actual.Length != entry.Length) throw new InvalidDataException("Expanded native file has an unexpected size.");
            await using var original = entry.Open();
            var originalHash = await SHA256.HashDataAsync(original, token);
            var actualHash = await SHA256.HashDataAsync(actual, token);
            if (!CryptographicOperations.FixedTimeEquals(originalHash, actualHash))
                throw new InvalidDataException("Expanded native file differs from its pinned source: " + relative);
        }
        expectedPaths.Add(Path.GetFullPath(Path.Combine(expanded, "image-utility-ready.json")));
        var pending = new Queue<string>();
        pending.Enqueue(expanded);
        while (pending.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                SafeUpdatePath.RejectLinks(path);
                if (Directory.Exists(path)) pending.Enqueue(path);
                else if (!expectedPaths.Contains(Path.GetFullPath(path)))
                    throw new InvalidDataException("Expanded native bundle contains an unlisted file.");
            }
        }
    }

    // Upstream archives legitimately contain models/; updater-owned root exclusions do not apply here.
    internal static string ResolveArchivePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 512 || relative.Contains('\\')
            || relative.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("Invalid native archive path.");
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Invalid native archive path component.");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native archive path escapes its root.");
        SafeUpdatePath.RejectLinks(fullPath);
        return fullPath;
    }
}
