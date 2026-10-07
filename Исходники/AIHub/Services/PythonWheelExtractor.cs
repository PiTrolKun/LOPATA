using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AIHub.Services;

/// <summary>Install exact wheel contents without executing pip or resolving additional packages.</summary>
internal static class PythonWheelExtractor
{
    internal static string RelativeDestination(string path, bool wheelLayout = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000 || path.Contains('\\') || path.Contains(':')
            || path.StartsWith('/') || path.Split('/').Any(UnsafeWindowsSegment))
            throw new InvalidDataException("Unsafe wheel entry path.");
        var parts = path.Split('/');
        if (!wheelLayout) return path;
        if (parts[0].EndsWith(".data", StringComparison.Ordinal))
        {
            if (parts.Length < 3) throw new InvalidDataException("Incomplete wheel data scheme.");
            var remainder = string.Join('/', parts.Skip(2));
            return parts[1] switch
            {
                "purelib" or "platlib" => "Lib/site-packages/" + remainder,
                "data" => remainder,
                "scripts" => "Scripts/" + remainder,
                "headers" => "Include/" + parts[0] + "/" + remainder,
                _ => throw new InvalidDataException("Unknown wheel installation scheme.")
            };
        }
        return "Lib/site-packages/" + path;
    }

    private static bool UnsafeWindowsSegment(string segment)
    {
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(character => character < 32 || "<>\"|?*".Contains(character))) return true;
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789\u00b9\u00b2\u00b3".Contains(stem[3]));
    }

    internal static async Task ExtractAsync(PinnedPythonArtifact artifact, string archivePath, string destination,
        CancellationToken token, bool wheelLayout = true)
    {
        // The component caller owns staging/rollback; no existing installation is modified here.
        await using var file = File.OpenRead(archivePath);
        if (file.Length != artifact.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase) == false)
            throw new InvalidDataException("Python wheel differs from its pinned upstream digest.");
        file.Position = 0; // Keep the verified file locked against replacement throughout extraction.
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(destination) || (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A normal staging directory is required.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        // The verified CUDA128 wheel expands beyond 4 GiB. Keep a finite bound
        // after enforcing the exact upstream archive digest above.
        if (archive.Entries.Count > 50000 || archive.Entries.Sum(entry => entry.Length) > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Python wheel exceeds extraction limits.");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Symlinks are not allowed in a Python wheel.");
            if (entry.FullName.EndsWith('/')) continue;
            var relative = RelativeDestination(entry.FullName, wheelLayout);
            var outputPath = Path.GetFullPath(Path.Combine(destination, relative));
            if (!outputPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !destinations.Add(outputPath)
                || File.Exists(outputPath))
                throw new InvalidDataException("Ambiguous or escaping wheel destination.");
            // Refuse pre-existing junctions anywhere between the staged root and output.
            for (var parent = Path.GetDirectoryName(outputPath); parent is not null && parent.Length >= root.Length;
                parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Wheel destination contains a reparse point.");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var input = entry.Open();
            await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous);
            var buffer = new byte[128 * 1024]; long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                written = checked(written + read);
                if (written > entry.Length) throw new InvalidDataException("Wheel entry exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated wheel entry.");
        }
    }
}
