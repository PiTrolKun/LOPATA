using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

internal sealed record AgentProduct(string Id, string Title, params string[] Aliases);

internal static class AgentClients
{
    internal static readonly AgentProduct[] Products = [
        new("codex", "Codex", "Codex Desktop", "codex-mcp-client", "codex-cli", "codex-app-server"),
        new("chatgpt", "ChatGPT", "ChatGPT Classic", "ChatGPT Desktop", "chatgpt-desktop"),
        new("claude", "Claude Desktop", "Claude", "Claude AI", "claude-ai", "claude-code"),
        new("antigravity", "Antigravity", "Google Antigravity", "Antigravity IDE"),
        new("qwen-code", "Qwen Code Desktop", "Qwen Code", "qwen-code-desktop"),
        new("deepseek", "DeepSeek Harness Desktop", "DeepSeek Harness", "deepseek-harness", "com.deepseek.dsh"),
        new("github-copilot", "GitHub Copilot", "GitHub Copilot Chat", "github.copilot", "github.copilot-chat", "copilot-cli"),
        new("cursor", "Cursor", "Cursor IDE", "cursor-vscode", "cursor-agent"),
        new("devin", "Devin Desktop", "Windsurf", "Windsurf Editor", "Devin Desktop (Windsurf)"),
        new("kiro", "Kiro", "Kiro IDE", "kiro-cli"),
        new("cherry", "Cherry Studio", "CherryStudio", "cherry-studio")
    ];

    internal static AgentProduct? Match(string? value) => Products.FirstOrDefault(p =>
        string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(p.Title, value, StringComparison.OrdinalIgnoreCase) ||
        p.Aliases.Contains(value, StringComparer.OrdinalIgnoreCase));

    internal static AgentProduct? Installed(string name, string shellId)
    {
        // The current Windows release displays OpenAI.Codex as ChatGPT in Start.
        if (shellId.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) return Match("codex");
        if (shellId.StartsWith("OpenAI.ChatGPT-Desktop_", StringComparison.OrdinalIgnoreCase)) return Match("chatgpt");
        return Match(name) ?? Match(shellId);
    }
}

internal sealed record AgentIdentity(string Name, string? Icon = null)
{
    internal static readonly AgentIdentity Anonymous = new("");
    internal const int MaxIconChars = 96 * 1024;

    internal static AgentIdentity ResolveCaller(AgentIdentity reported, string? codexThread, string? codexToolCall)
    {
        if (reported.Name.Length > 0) return reported;
        // Both identifiers are supplied to a command executed by the Codex tool runner.
        return !string.IsNullOrWhiteSpace(codexThread) && !string.IsNullOrWhiteSpace(codexToolCall)
            ? reported with { Name = "Codex" } : reported;
    }

    internal static AgentIdentity FromName(string? raw)
    {
        var name = new string((raw ?? "").Where(c => !char.IsControl(c) &&
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(80).ToArray()).Trim();
        return new(AgentClients.Match(name)?.Title ?? name);
    }

    internal static AgentIdentity FromHandshake(JsonElement parameters, AgentIdentity configured)
    {
        if (!parameters.TryGetProperty("clientInfo", out var info) || info.ValueKind != JsonValueKind.Object)
            return configured;
        var name = FromName(Text(info, "name"));
        var title = FromName(Text(info, "title"));
        var identity = configured.Name.Length > 0 ? configured
            : AgentClients.Match(name.Name) != null || title.Name.Length == 0 ? name : title;
        if (!info.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Array) return identity;
        foreach (var icon in icons.EnumerateArray())
        {
            if (icon.ValueKind != JsonValueKind.Object) continue;
            var uri = Text(icon, "src");
            if (uri is { Length: <= MaxIconChars } && (uri.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)))
                return identity with { Icon = uri };
        }
        return identity;
    }

    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
