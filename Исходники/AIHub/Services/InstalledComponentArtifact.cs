using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Resolve the same nested archive layout accepted by component health checks.</summary>
public static class InstalledComponentArtifact
{
    public static string Resolve(ComponentStatusSnapshot status)
    {
        var root = status.Record.InstallPath;
        var relative = status.Entry.HealthCheckRelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (!status.IsAvailable || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative))
            throw new FileNotFoundException("Component artifact is unavailable: " + status.Entry.Id);
        var expectedName = Path.GetFileName(relative);
        if (File.Exists(root) && Path.GetFileName(root).Equals(expectedName, StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new FileNotFoundException("Component directory is missing: " + status.Entry.Id);
        var direct = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!direct.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe component artifact path.");
        if (File.Exists(direct)) return direct;
        var matches = Directory.EnumerateFiles(root, expectedName, SearchOption.AllDirectories).Take(2).ToArray();
        if (matches.Length != 1) throw new FileNotFoundException("Missing or ambiguous component artifact: " + status.Entry.Id);
        return Path.GetFullPath(matches[0]);
    }
}
