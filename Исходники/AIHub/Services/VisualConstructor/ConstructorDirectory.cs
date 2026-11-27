using System.Diagnostics;
using System.IO;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed class ConstructorDirectory : IDisposable
{
    private readonly string _parent;
    private bool _moved;

    private ConstructorDirectory(string parent)
    {
        _parent = Path.GetFullPath(parent);
        SafeUpdatePath.RejectLinks(_parent);
        FullPath = Path.Combine(_parent, $".lopata-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FullPath);
    }

    internal string FullPath { get; }
    internal static ConstructorDirectory Create(string parent) => new(parent);

    internal void MoveTo(string destination)
    {
        if (!Path.GetDirectoryName(Path.GetFullPath(destination))!.Equals(
            Path.TrimEndingDirectorySeparator(_parent), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The destination must be a sibling of the prepared directory.", nameof(destination));
        SafeUpdatePath.RejectLinks(FullPath);
        SafeUpdatePath.RejectLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("VisualConstructor.AlreadyExists");
        Directory.Move(FullPath, destination);
        _moved = true;
    }

    internal static bool IsWithin(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_moved || !IsWithin(_parent, FullPath) || FullPath == _parent) return;
        try
        {
            SafeUpdatePath.RejectLinks(FullPath);
            if (Directory.Exists(FullPath)) Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Visual constructor staging cleanup failed: {0}", ex.Message);
        }
    }
}
