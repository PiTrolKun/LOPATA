using System.IO;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Lopata.Updates;

namespace AIHub.Services;

/// <summary>One process-wide selection. Each operation must capture ActiveId once before resolving its files.</summary>
public static class EngineSetSelection
{
    private static string _activeId = "002";
    private static string _recommendedId = "002";
    private static readonly ConcurrentDictionary<string, string> VerifiedDirectories = new(StringComparer.Ordinal);
    public static string ActiveId => Volatile.Read(ref _activeId);
    public static string RecommendedId => Volatile.Read(ref _recommendedId);
    internal static void RegisterDirectory(string setId, string path) => VerifiedDirectories[setId] = Path.GetFullPath(path);
    internal static void Publish(string activeId, string recommendedId)
    {
        ValidateId(activeId); ValidateId(recommendedId);
        Volatile.Write(ref _recommendedId, recommendedId);
        Volatile.Write(ref _activeId, activeId);
    }
    public static string ResolveDirectory(string setId, string family, string flavor)
    {
        ValidateId(setId);
        if (!Regex.IsMatch(family ?? "", "^[a-z][a-z0-9-]{0,30}$")
            || !Regex.IsMatch(flavor ?? "", "^[a-z][a-z0-9-]{0,30}$"))
            throw new InvalidDataException("Invalid engine component identifier.");
        var optional = SafeUpdatePath.Resolve(Path.Combine(AppDataPaths.RuntimeDirectory, "EngineSets"), setId);
        var bundled = SafeUpdatePath.Resolve(Path.Combine(AppContext.BaseDirectory, "EngineSets"), setId);
        var root = VerifiedDirectories.TryGetValue(setId, out var verified) && Directory.Exists(verified)
            ? verified : Directory.Exists(optional) ? optional : bundled;
        return SafeUpdatePath.Resolve(root, family + "/" + flavor);
    }
    internal static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[0-9]{3}$") || id == "000")
            throw new InvalidDataException("Invalid engine set identifier.");
    }
}
