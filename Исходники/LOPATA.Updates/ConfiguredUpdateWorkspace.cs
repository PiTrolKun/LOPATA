using System.Text.Json;

namespace Lopata.Updates;

public static class ConfiguredUpdateWorkspace
{
    public static string Resolve(string profile, string legacyDirectory)
    {
        var path = Path.Combine(profile, "storage-settings.json");
        if (!File.Exists(path)) return legacyDirectory;
        SafeUpdatePath.RejectLinks(path);
        using var settings = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!settings.RootElement.TryGetProperty("results", out var results) ||
            !results.TryGetProperty("locations", out var locations)) return legacyDirectory;
        foreach (var location in locations.EnumerateArray())
        {
            if (!location.TryGetProperty("path", out var value) || value.GetString()?.Trim() is not { Length: > 0 } root)
                continue;
            if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("Update storage must be an absolute path.");
            if (!Directory.Exists(root)) throw new IOException("Update storage is unavailable: " + root);
            var workspace = Path.Combine(Path.GetFullPath(root), "AI_HUB", "Temporary", "Updates", Path.GetFileName(legacyDirectory));
            SafeUpdatePath.RejectLinks(workspace);
            return workspace;
        }
        return legacyDirectory;
    }
}
