namespace Lopata.Collection;

/// <summary>Continues a bounded folder pass on the next scan instead of repeating its prefix.</summary>
internal sealed class CollectionKnownCursor : IDisposable
{
    private readonly Queue<string> _directories = new();
    private readonly HashSet<string> _seenDirectories = new(StringComparer.OrdinalIgnoreCase);
    private IEnumerator<FileSystemInfo>? _children;
    public bool Complete { get; private set; }

    public void Reset(CollectionConfig config)
    {
        Dispose(); _directories.Clear(); _seenDirectories.Clear(); Complete = false;
        foreach (var root in config.Roots.Distinct(StringComparer.OrdinalIgnoreCase)) _directories.Enqueue(root);
    }

    public bool MoveNext(CollectionConfig config, out string? path)
    {
        path = null;
        while (_children is null)
        {
            if (!_directories.TryDequeue(out var directory)) { Complete = true; return false; }
            if (!_seenDirectories.Add(directory) || !CollectionFilePolicy.IsAllowed(directory, config)) continue;
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.Exists && CollectionFilePolicy.CanRead(info)) _children = info.EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (_children is null) return true;
        }
        try
        {
            if (!_children.MoveNext()) { Dispose(); return true; }
            var child = _children.Current;
            if (!CollectionFilePolicy.CanRead(child) || !CollectionFilePolicy.IsAllowed(child.FullName, config)) return true;
            if (child is DirectoryInfo) _directories.Enqueue(child.FullName);
            else path = child.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Dispose(); }
        return true;
    }

    public void Dispose()
    {
        _children?.Dispose();
        _children = null;
    }
}
