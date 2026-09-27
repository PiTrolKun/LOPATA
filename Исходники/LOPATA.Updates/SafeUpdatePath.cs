using System.Text.RegularExpressions;

namespace Lopata.Updates;

public static class SafeUpdatePath
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lopata-update", "Models", "Projects", "UserData", "Diagnostics", "Logs", "Qdrant",
        "settings.json", "appsettings.json", "unins000.exe", "unins000.dat"
    };

    public static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 200 || path.Contains('\\')
            || path.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("Invalid update path.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || ProtectedNames.Contains(segment)
                || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])($|\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("Reserved update path.");
        }
    }

    public static string Resolve(string root, string relative)
    {
        ValidateRelative(relative);
        return ResolveInternal(root, relative);
    }

    internal static string ResolveInternal(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update path escapes installation root.");
        RejectLinks(fullPath);
        return fullPath;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            // GetAttributes also detects dangling reparse points, unlike File.Exists.
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Update paths cannot traverse links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

public sealed class UpdateRoots
{
    private readonly Dictionary<string, string> _paths;

    public UpdateRoots(IReadOnlyDictionary<string, string> paths)
    {
        _paths = new(StringComparer.Ordinal);
        foreach (var (id, path) in paths)
        {
            if (id is not ("app" or "llama" or "chatllm") || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("Invalid installation root.");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= (Path.GetPathRoot(full)?.Length ?? 0))
                throw new InvalidDataException("A drive root cannot be an installation directory.");
            SafeUpdatePath.RejectLinks(full);
            _paths.Add(id, full);
        }
        if (!_paths.ContainsKey("app")) throw new InvalidDataException("Missing application directory.");
        var directories = _paths.Values.Select(p => p + Path.DirectorySeparatorChar).ToArray();
        for (var i = 0; i < directories.Length; i++)
            for (var j = i + 1; j < directories.Length; j++)
                if (directories[i].StartsWith(directories[j], StringComparison.OrdinalIgnoreCase)
                    || directories[j].StartsWith(directories[i], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installation roots must not overlap.");
    }

    public string Root(string id) => _paths.TryGetValue(id, out var path) ? path
        : throw new InvalidDataException("Missing installation root: " + id);
    public string Resolve(UpdateFile file) => SafeUpdatePath.Resolve(Root(file.Root), file.Path);
    internal IEnumerable<string> RootIds => _paths.Keys.Order(StringComparer.Ordinal);
    internal string Internal(string id, string relative) => SafeUpdatePath.ResolveInternal(Root(id), relative);
}
