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
        if (args.Length != 0)
        {
            try
            {
                object result;
                if (args is ["--tools"]) result = AgentTools.List();
                else if (args is ["--call", var name])
                {
                    var input = await Console.In.ReadToEndAsync();
                    var parameters = string.IsNullOrWhiteSpace(input) ? Empty : JsonSerializer.Deserialize<JsonElement>(input);
                    result = await AgentTools.CallAsync(name, parameters);
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
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement? id = null;
            try
            {
                using var document = JsonDocument.Parse(line);
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
                        result = new
                        {
                            protocolVersion = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" ? requested : ProtocolVersion,
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = "lopata", version = ApplicationBuildVersion.Value },
                            instructions = "Ask project_instructions for the selected scenario. Use logs_search and logs_read when the user asks about a failure."
                        };
                        initialized = true;
                        break;
                    case "ping": result = Empty; break;
                    case "tools/list" when initialized: result = new { tools = AgentTools.List() }; break;
                    case "tools/call" when initialized:
                        var tool = AgentTools.RequiredString(parameters, "name");
                        try
                        {
                            var answer = await AgentTools.CallAsync(tool, parameters.TryGetProperty("arguments", out var arguments) ? arguments : Empty);
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
