using System.IO;

namespace AIHub.Services.Books.Smart;

/// <summary>Window-scoped readiness receipt; never persisted as an unconditional ready flag.</summary>
internal sealed class BookSmartPreparedSession
{
    private sealed record Stamp(string Path, long Size, DateTime Modified);
    private readonly string _root, _engine;
    private readonly BookSmartSelection _selection;
    private readonly bool _images;
    private readonly Stamp[] _files;

    internal BookSmartPreparedSession(string root, BookSmartSelection selection, bool images, string engine, IEnumerable<string> paths)
    {
        _root = Path.GetFullPath(root); _selection = selection; _images = images; _engine = engine;
        _files = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Prepared book component is missing.", path);
            return new Stamp(info.FullName, info.Length, info.LastWriteTimeUtc);
        }).ToArray();
    }

    internal bool Matches(string root, BookSmartSelection selection, bool images, string engine, IEnumerable<string>? paths)
    {
        if (paths is null || !Path.GetFullPath(root).Equals(_root, StringComparison.OrdinalIgnoreCase)
            || selection != _selection || images && !_images || engine != _engine || _files.Length == 0) return false;
        if (!paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_files.Select(file => file.Path))) return false;
        return _files.All(file =>
        {
            var info = new FileInfo(file.Path);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.Modified;
        });
    }
}
