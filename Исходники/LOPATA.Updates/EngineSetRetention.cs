using System.Text.RegularExpressions;

namespace Lopata.Updates;

/// <summary>Transfers retired runtime ownership to the optional component catalog, without making it mandatory for fresh installs.</summary>
public static class EngineSetRetention
{
    public static bool IsVersioned(UpdateFile file) => file.Root == "app"
        && Regex.IsMatch(file.Path, @"^EngineSets/(?!000/)[0-9]{3}/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool PreserveAfterUpdate(UpdateFile file) => IsVersioned(file)
        || file.Root == "app" && file.Path.StartsWith("MusicStudioRuntime/", StringComparison.OrdinalIgnoreCase);
}
