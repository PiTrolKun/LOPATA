using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

internal sealed class AgentConnectionSetup(string applicationDirectory, string profileDirectory)
{
#if DEBUG
    internal const string PluginName = "lopata-dev";
    internal const string DisplayName = "ЛОПАТА (dev)";
#else
    internal const string PluginName = "lopata";
    internal const string DisplayName = "ЛОПАТА";
#endif
    internal const string MarketName = PluginName + "-local";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _agent = Path.GetFullPath(Path.Combine(applicationDirectory, "Lopata.Agent.exe"));
    private readonly string _assets = Path.Combine(applicationDirectory, "Assets", "ExternalAgents");
    private readonly string _root = Path.Combine(profileDirectory, "ExternalAgents", PluginName);

    internal string MarketplaceRoot => Path.Combine(_root, "Marketplace");
    internal string MarketplaceManifest => Path.Combine(MarketplaceRoot, ".agents", "plugins", "marketplace.json");
    private string RegistrationFile => Path.Combine(_root, "registered.txt");

    internal bool HasRegistration => File.Exists(MarketplaceManifest) && File.Exists(RegistrationFile)
        && string.Equals(File.ReadAllText(RegistrationFile), _agent, StringComparison.OrdinalIgnoreCase);

    internal void RecordRegistration() => ReplaceFile(RegistrationFile, Encoding.UTF8.GetBytes(_agent));

    internal static AgentConnectionSetup Current() => new(AppContext.BaseDirectory, AppDataPaths.BaseDirectory);

    internal void PrepareMarketplace(string description)
    {
        EnsureAgent();
        var plugin = Path.Combine(MarketplaceRoot, "plugins", PluginName);
        var version = typeof(AgentConnectionSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        WriteAsset(plugin, "SKILL.md", "skills", "lopata");
        WriteAsset(plugin, "emblem.png", "assets");
        WriteJson(Path.Combine(plugin, ".mcp.json"), new
        {
            mcpServers = new Dictionary<string, object> { [PluginName] = new { command = _agent, args = Array.Empty<string>() } }
        });
        WriteJson(Path.Combine(plugin, ".codex-plugin", "plugin.json"), new
        {
            name = PluginName, version, description, author = new { name = "LOPATA" },
            skills = "./skills/", mcpServers = "./.mcp.json",
            @interface = new
            {
                displayName = DisplayName, shortDescription = description, category = "Productivity",
                developerName = "LOPATA", composerIcon = "./assets/emblem.png", logo = "./assets/emblem.png"
            }
        });
        WriteJson(MarketplaceManifest, new
        {
            name = MarketName, @interface = new { displayName = DisplayName },
            plugins = new[] { new {
                name = PluginName, source = new { source = "local", path = "./plugins/" + PluginName },
                policy = new { installation = "AVAILABLE", authentication = "ON_INSTALL" }, category = "Productivity"
            } }
        });
    }

    internal string CreateInstructions()
    {
        EnsureAgent();
        var skill = File.ReadAllText(Path.Combine(_assets, "SKILL.md"));
        var bodyStart = skill.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (bodyStart < 0) throw new InvalidDataException("Invalid LOPATA skill.");
        var tools = skill[(bodyStart + 4)..].TrimStart();
        var instructions = $"""
            # LOPATA — local agent connection

            Use this installed application through MCP over stdio. Launch this exact executable directly with no arguments:

                {_agent}

            Initialize the MCP session, list its tools and use lopata_info to verify this copy. A client that can launch local processes may instead run the same executable with --tools or --call TOOL_NAME. For --call, pass a JSON object encoded as UTF-8 through standard input, then close standard input. Pass arguments as individual values; never execute a project description as shell code.

            {tools}
            """;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)))[..16];
        var folder = Path.Combine(_root, "Instructions", digest);
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "AGENTS.md");
        if (File.Exists(destination) && File.ReadAllText(destination) == instructions) return destination;
        if (File.Exists(destination))
        {
            // The user edited this copy. Keep it intact and hand out a fresh instruction.
            folder = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            destination = Path.Combine(folder, "AGENTS.md");
        }
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        stream.Write(Encoding.UTF8.GetBytes(instructions));
        return destination;
    }

    private void EnsureAgent()
    {
        if (!File.Exists(_agent)) throw new FileNotFoundException(Path.GetFileName(_agent), _agent);
    }

    private void WriteAsset(string plugin, string name, params string[] directories)
    {
        var directory = Path.Combine([plugin, .. directories]);
        ReplaceFile(Path.Combine(directory, name), File.ReadAllBytes(Path.Combine(_assets, name)));
    }

    private static void WriteJson(string path, object value) =>
        ReplaceFile(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static void ReplaceFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
