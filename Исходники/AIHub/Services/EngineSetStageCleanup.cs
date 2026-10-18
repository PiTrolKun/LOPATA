using System.IO;
using System.Text.RegularExpressions;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>Only the unique preparation directory owned by one call is expendable; cached downloads and installed sets are not.</summary>
internal static class EngineSetStageCleanup
{
    internal static void DeleteOwned(string installedRoot, string stagePath, string expectedLeaf)
    {
        if (!Path.IsPathFullyQualified(installedRoot) || !Path.IsPathFullyQualified(stagePath)
            || !Regex.IsMatch(expectedLeaf, @"^\.prepare-(?!000-)[0-9]{3}-[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid owned engine preparation path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installedRoot));
        var stage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagePath));
        if (!string.Equals(Path.GetFileName(stage), expectedLeaf, StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(stage), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Engine preparation cleanup escaped its owner root.");
        SafeUpdatePath.RejectLinks(stage);
        if (!Directory.Exists(stage)) return;
        var prefix = stage + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>(); pending.Push(stage);
        // Validate the complete tree before removing even its first file. Never recurse through a link.
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var path = Path.GetFullPath(entry);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Engine preparation child escaped its owner directory.");
                SafeUpdatePath.RejectLinks(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                { directories.Add(path); pending.Push(path); }
                else files.Add(path);
            }
        foreach (var file in files) { SafeUpdatePath.RejectLinks(file); File.Delete(file); }
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        { SafeUpdatePath.RejectLinks(directory); Directory.Delete(directory, false); }
        SafeUpdatePath.RejectLinks(stage);
        Directory.Delete(stage, false);
    }
}
