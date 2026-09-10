using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public enum LiteraryProjectRemoval { KeepFiles, DeleteFiles }

/// <summary>Deletes only a registered, identity-checked project directory; never follows links.</summary>
internal static class LiteraryProjectDeletion
{
    public static void Delete(LiteraryProjectEntry entry, IReadOnlyList<LiteraryProjectEntry> projects, string indexPath)
    {
        if (!Path.IsPathFullyQualified(entry.ProjectPath)) throw new IOException("Project path must be absolute.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.ProjectPath));
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)
            || Inside(root, Path.GetFullPath(indexPath))) throw new IOException("Unsafe project directory.");
        foreach (var other in projects.Where(p => p.Id != entry.Id))
            if (Inside(root, Path.GetFullPath(other.ProjectPath)))
                throw new IOException("The directory contains another registered project.");
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists) CheckLink(parent.FullName);
        if (!Directory.Exists(root))
        {
            if (File.Exists(root)) throw new IOException("Project directory has been replaced by a file.");
            return; // Already removed externally: retire the stale registration.
        }

        var manifest = Path.Combine(root, "project.json");
        CheckLink(manifest);
        if (LiteraryProjectStore.ReadProject(root).Id != entry.Id)
            throw new IOException("Project identity does not match its registration.");

        var files = new List<string>();
        var directories = new List<string>();
        void Inspect(string directory)
        {
            CheckLink(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckLink(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) Inspect(path);
                else files.Add(path);
            }
            directories.Add(directory); // Children before parents.
        }
        Inspect(root);
        // Catch common locks/read-only files before removing any content.
        foreach (var file in files)
        {
            if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                throw new IOException("A project file is read-only: " + file);
            using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        foreach (var file in files.Where(p => !string.Equals(p, manifest, StringComparison.OrdinalIgnoreCase)))
        {
            CheckLink(file);
            File.Delete(file);
        }
        foreach (var directory in directories.Where(p => p != root))
        {
            CheckLink(directory);
            Directory.Delete(directory, recursive: false);
        }
        // Keep the identity marker until all content has been removed, allowing a failed cleanup to be retried.
        CheckLink(manifest);
        File.Delete(manifest);
        Directory.Delete(root, recursive: false);
    }

    private static bool Inside(string root, string path) => string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Project deletion does not follow symbolic links or junctions: " + path);
    }
}
