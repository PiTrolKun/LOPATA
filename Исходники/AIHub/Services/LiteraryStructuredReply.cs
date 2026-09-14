using System.Text.Json;

namespace AIHub.Services;

public static class LiteraryStructuredReply
{
    /// <summary>Accept a whole JSON code fence, never extract JSON from surrounding prose.</summary>
    public static string Json(string reply)
    {
        var text = reply.Trim();
        var newline = text.IndexOf('\n');
        if (newline >= 0 && text.EndsWith("```", StringComparison.Ordinal))
        {
            var header = text[..newline].TrimEnd('\r');
            if (header is "```json" or "```") text = text[(newline + 1)..^3].Trim();
        }
        using var document = JsonDocument.Parse(text);
        return text;
    }
}
