using System.IO;
using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

internal static class CodexPluginConnection
{
    // registered.txt records opening the marketplace, which precedes the user's Install decision.
    internal static bool? Read(string profile, string executable)
    {
        var plugin = AgentConnectionSetup.PluginName;
        var market = AgentConnectionSetup.MarketName;
        var section = "plugins.\"" + plugin + "@" + market + "\"";
        var config = Path.Combine(profile, "config.toml");
        try
        {
            if (!File.Exists(config)) return null;
            var selected = false;
            bool? enabled = null;
            foreach (var line in File.ReadLines(config))
            {
                var text = line.Split('#', 2)[0].Trim();
                if (text.StartsWith('[')) selected = text.Trim('[', ']').Replace('\'', '"') == section;
                else if (selected && text.Split('=', 2, StringSplitOptions.TrimEntries) is ["enabled", var value]
                    && bool.TryParse(value.Trim(), out var flag)) enabled = flag;
            }
            if (enabled != true) return enabled;
            var cache = Path.Combine(profile, "plugins", "cache", market, plugin);
            if (!Directory.Exists(cache)) return false;
            var manifests = Directory.EnumerateDirectories(cache)
                .Select(dir => new FileInfo(Path.Combine(dir, ".mcp.json"))).Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc);
            var manifest = manifests.FirstOrDefault();
            if (manifest == null) return false;
            using var file = JsonDocument.Parse(File.ReadAllText(manifest.FullName));
            return file.RootElement.GetProperty("mcpServers").GetProperty(plugin).GetProperty("command").GetString()
                is { } command && Path.IsPathFullyQualified(command) && string.Equals(
                    Path.GetFullPath(command), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or ArgumentException) { return null; }
    }

    internal static bool? ReadCurrent()
    {
        var profile = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(profile))
            profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return Read(profile, Path.Combine(AppContext.BaseDirectory, "Lopata.Agent.exe"));
    }
}
