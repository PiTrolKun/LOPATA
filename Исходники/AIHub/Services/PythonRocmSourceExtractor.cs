using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AIHub.Services;

/// <summary>Install the pinned pure-Python ROCm module without executing upstream setup.py.</summary>
internal static class PythonRocmSourceExtractor
{
    internal static async Task ExtractAsync(PinnedPythonArtifact artifact, string archivePath, string destination,
        CancellationToken token)
    {
        if (artifact.Name != "rocm" || artifact.Version != "7.2.1" || artifact.FileName != "rocm-7.2.1.tar.gz"
            || artifact.Bytes > 1024 * 1024)
            throw new InvalidDataException("Unexpected ROCm source artifact.");
        await using var file = File.OpenRead(archivePath);
        if (file.Length != artifact.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ROCm source differs from its pinned upstream digest.");
        file.Position = 0;
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true))
        using (var reader = new TarReader(gzip))
        {
            long total = 0; int count = 0;
            while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
            {
                if (++count > 50 || entry.Length > 1024 * 1024 || (total = checked(total + entry.Length)) > 1024 * 1024)
                    throw new InvalidDataException("ROCm source exceeds extraction limits.");
                var name = entry.Name.TrimEnd('/');
                _ = PythonWheelExtractor.RelativeDestination(name, wheelLayout: false);
                if (name != "rocm-7.2.1" && !name.StartsWith("rocm-7.2.1/", StringComparison.Ordinal))
                    throw new InvalidDataException("ROCm source has an unexpected root.");
                if (entry.EntryType == TarEntryType.Directory) continue;
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || (entry.Length != 0 && entry.DataStream is null) || !contents.TryAdd(name, new byte[checked((int)entry.Length)]))
                    throw new InvalidDataException("ROCm source contains a link or ambiguous entry.");
                if (entry.Length != 0) await entry.DataStream!.ReadExactlyAsync(contents[name], token);
            }
        }
        const string prefix = "rocm-7.2.1/src/rocm_sdk/";
        foreach (var required in new[] { "__init__.py", "__main__.py", "_devel.py", "_dist_info.py" })
            if (!contents.ContainsKey(prefix + required)) throw new InvalidDataException("Incomplete ROCm Python sources.");
        if (!contents.TryGetValue("rocm-7.2.1/PKG-INFO", out var metadata)
            || !System.Text.Encoding.UTF8.GetString(metadata).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Contains("\nName: rocm\nVersion: 7.2.1\n", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected ROCm package metadata.");
        foreach (var (name, data) in contents.Where(row => row.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!name.EndsWith(".py", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected ROCm Python payload.");
            await WriteNewAsync(destination, "Lib/site-packages/rocm_sdk/" + name[prefix.Length..], data, token);
        }
        await WriteNewAsync(destination, "Lib/site-packages/rocm-7.2.1.dist-info/METADATA", metadata, token);
        // Preserve the complete original source archive, including setup.py and package metadata.
        file.Position = 0;
        var archiveCopy = NewPath(destination, "Notices/rocm-7.2.1.tar.gz");
        await using var output = new FileStream(archiveCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(output, token);
    }

    private static async Task WriteNewAsync(string root, string relative, byte[] data, CancellationToken token)
    {
        await using var output = new FileStream(NewPath(root, relative), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(data, token);
    }

    private static string NewPath(string directory, string relative)
    {
        _ = PythonWheelExtractor.RelativeDestination(relative, wheelLayout: false);
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || File.Exists(path) || !Directory.Exists(root)) throw new InvalidDataException("Unsafe ROCm staging destination.");
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ROCm destination contains a reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
