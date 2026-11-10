using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OneC;

internal sealed record OneCSourceModule(string Path, string RelativeName, string Kind);
internal interface IOneCProjectAdapter
{
    string Id { get; }
    bool Supports(string path);
    Task<IReadOnlyList<OneCSourceModule>> ListAsync(string path, CancellationToken token);
}

/// <summary>Working read-only extension example; unknown folders remain ordinary text material collections.</summary>
internal sealed class OneCTextProjectAdapter : IOneCProjectAdapter
{
    public string Id => "onec.text-folder.v1";
    public bool Supports(string path) => Directory.Exists(path);
    public Task<IReadOnlyList<OneCSourceModule>> ListAsync(string path, CancellationToken token) => Task.Run<IReadOnlyList<OneCSourceModule>>(() =>
    {
        var root = System.IO.Path.GetFullPath(path); Lopata.Updates.SafeUpdatePath.RejectLinks(root);
        var result = new List<OneCSourceModule>(); var visited = 0;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 5000) throw new InvalidDataException("OneC.InputLimit");
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(child);
                var relative = System.IO.Path.GetRelativePath(root, child);
                if (relative.Split(System.IO.Path.DirectorySeparatorChar).Length > 24) throw new InvalidDataException("OneC.InputLimit");
                if (System.IO.Path.GetFileName(child) is not (".git" or "bin" or "obj")) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".bsl" or ".os" or ".xml")) continue;
                Lopata.Updates.SafeUpdatePath.RejectLinks(file);
                if (new FileInfo(file).Length > 2_000_000 || result.Count >= 500) throw new InvalidDataException("OneC.InputLimit");
                if (extension == ".xml")
                {
                    using var reader = XmlReader.Create(file, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                    while (reader.Read()) if (reader.Depth > 64) throw new InvalidDataException("OneC.InputLimit");
                }
                result.Add(new(file, System.IO.Path.GetRelativePath(root, file), extension == ".xml" ? "xml-material" : "bsl-module"));
            }
        }
        return result;
    }, token);
}

internal static class OneCAdapters
{
    // Register source/checker extensions here; the UI depends only on the contracts.
    internal static IReadOnlyList<IOneCProjectAdapter> Sources { get; } = [new OneCTextProjectAdapter()];
    internal static IReadOnlyDictionary<string, Func<IOneCCodeChecker>> Checkers { get; } =
        new Dictionary<string, Func<IOneCCodeChecker>> { [OneCBslRuntime.ComponentId] = () => new OneCBslChecker() };
    internal static IOneCCodeChecker CreateChecker(string id = OneCBslRuntime.ComponentId)
        => Checkers.TryGetValue(id, out var create) ? create() : throw new InvalidDataException("OneC.UnsupportedCapability");
    internal static readonly string[] Capabilities = ["discover", "list", "read", "check", "createOwnedFile", "editOwnedFile"];
    internal static void Require(int protocolVersion, string capability)
    {
        if (protocolVersion != 1) throw new InvalidDataException("OneC.UnsupportedVersion");
        if (!Capabilities.Contains(capability, StringComparer.Ordinal)) throw new InvalidDataException("OneC.UnsupportedCapability");
    }
}
