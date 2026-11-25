using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

internal static class AgentProtocol
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args)
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        var configured = AgentIdentity.Anonymous;
        var codexPlugin = args is ["--codex-plugin"];
        if (codexPlugin)
        {
            configured = AgentIdentity.FromName("Codex");
            args = [];
        }
        else if (args is ["--client", var clientName, ..])
        {
            configured = AgentIdentity.FromName(clientName);
            args = args[2..];
        }
        AgentIdentity Caller(AgentIdentity identity) => AgentIdentity.ResolveCaller(identity,
            Environment.GetEnvironmentVariable("CODEX_THREAD_ID"), Environment.GetEnvironmentVariable("CODEX_TOOL_CALL_ID"));
        var client = Caller(configured);
        if (args.Length != 0)
        {
            try
            {
                object result;
                if (args is ["--tools"]) result = AgentTools.List();
                else if (args is ["--relay"])
                {
                    using var relay = JsonDocument.Parse(await Console.In.ReadToEndAsync());
                    var request = relay.RootElement;
                    if (request.GetProperty("build").Deserialize<AgentInstallation.Build>(Json) != AgentInstallation.Running)
                        throw new IOException(AgentInstallation.UpdateInProgress);
                    client = AgentIdentity.FromHandshake(request, AgentIdentity.Anonymous);
                    result = request.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String
                        ? await AgentTools.CallAsync(tool.GetString()!, request.GetProperty("arguments"), client)
                        : AgentTools.List();
                }
                else if (args is ["--call", var name])
                {
                    var input = (await Console.In.ReadToEndAsync()).TrimStart('\uFEFF');
                    var parameters = string.IsNullOrWhiteSpace(input) ? Empty : JsonSerializer.Deserialize<JsonElement>(input);
                    result = await AgentTools.CallAsync(name, parameters, client);
                    AgentPresenceStore.Current.Remember(client);
                }
                else throw new ArgumentException("Use MCP over stdio, --tools, or --call TOOL with a JSON object on stdin.");
                await WriteAsync(result);
                return 0;
            }
            catch (Exception ex)
            {
                await WriteAsync(new { error = ex.Message });
                return 1;
            }
        }

        bool initialized = false;
        var advertisedBuild = AgentInstallation.Running;
        using var poll = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var nextPoll = poll.WaitForNextTickAsync().AsTask();
        using var mcpInput = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var nextLine = mcpInput.ReadLineAsync();
        while (true)
        {
            var completed = await Task.WhenAny(nextPoll, nextLine);
            if (completed == nextPoll)
            {
                nextPoll = poll.WaitForNextTickAsync().AsTask();
                if (initialized) await RefreshToolsAsync(AgentInstallation.ReadBuild());
                continue;
            }
            var line = await nextLine;
            if (line is null) break;
            nextLine = mcpInput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement? id = null;
            try
            {
                using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                var request = document.RootElement;
                if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonrpc", out var version)
                    || !version.ValueEquals("2.0") || !request.TryGetProperty("method", out var method)
                    || method.ValueKind != JsonValueKind.String)
                {
                    await ErrorAsync(null, -32600, "Invalid JSON-RPC request");
                    continue;
                }
                if (!request.TryGetProperty("id", out var requestId)) continue;
                id = requestId.Clone();
                var parameters = request.TryGetProperty("params", out var input) ? input : Empty;
                if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected params object");
                object result;
                switch (method.GetString())
                {
                    case "initialize":
                        var requested = parameters.TryGetProperty("protocolVersion", out var pv) ? pv.GetString() : null;
                        client = Caller(AgentIdentity.FromHandshake(parameters, configured));
                        if (!codexPlugin) AgentPresenceStore.Current.Remember(client);
                        advertisedBuild = AgentInstallation.ReadBuild() ?? AgentInstallation.Running;
                        result = new
                        {
                            protocolVersion = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requested : ProtocolVersion,
                            capabilities = new { tools = new { listChanged = true } },
                            serverInfo = new { name = "lopata", version = advertisedBuild.Version },
                            instructions = "Call lopata_info for the current installed build and capabilities revision. Ask project_instructions for the current fields before creating a project. After tools/list_changed, refresh the tools and scenario instructions. Use logs_search and logs_read for failures."
                        };
                        initialized = true;
                        break;
                    case "ping": result = Empty; break;
                    case "tools/list" when initialized:
                        var listBuild = await CurrentToolsAsync();
                        result = new { tools = listBuild == AgentInstallation.Running ? AgentTools.List()
                            : await AgentInstallation.CallAsync(listBuild, null, Empty, client) };
                        break;
                    case "tools/call" when initialized:
                        var tool = AgentTools.RequiredString(parameters, "name");
                        try
                        {
                            var currentBuild = await CurrentToolsAsync();
                            var arguments = parameters.TryGetProperty("arguments", out var supplied) ? supplied : Empty;
                            var answer = currentBuild == AgentInstallation.Running
                                ? await AgentTools.CallAsync(tool, arguments, client)
                                : await AgentInstallation.CallAsync(currentBuild, tool, arguments, client);
                            result = ToolResult(JsonSerializer.Serialize(answer, Json), false);
                        }
                        catch (Exception ex) { result = ToolResult(ex.Message, true); }
                        break;
                    default:
                        await ErrorAsync(id, -32601, initialized ? "Unknown method" : "Initialize the connection first");
                        continue;
                }
                await WriteAsync(new { jsonrpc = "2.0", id, result });
            }
            catch (JsonException) { await ErrorAsync(id, -32700, "Invalid JSON"); }
            catch (Exception ex) { await ErrorAsync(id, -32602, ex.Message); }
        }
        return 0;

        async Task RefreshToolsAsync(AgentInstallation.Build? build)
        {
            if (build is null || build == advertisedBuild) return;
            advertisedBuild = build;
            await WriteAsync(new { jsonrpc = "2.0", method = "notifications/tools/list_changed" });
        }

        async Task<AgentInstallation.Build> CurrentToolsAsync()
        {
            var build = AgentInstallation.ReadBuild() ?? throw new IOException(AgentInstallation.UpdateInProgress);
            await RefreshToolsAsync(build);
            return build;
        }
    }

    private static object ToolResult(string text, bool isError) => new { content = new[] { new { type = "text", text } }, isError };
    private static Task ErrorAsync(JsonElement? id, int code, string message) =>
        WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } });
    private static async Task WriteAsync(object value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        await Console.Out.FlushAsync();
    }
}
