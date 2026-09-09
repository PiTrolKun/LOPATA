using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Transport only: no sampling overrides, answer filtering or image-analysis contracts.</summary>
public static class LiteraryRawProtocol
{
    public static string BuildRequest(IReadOnlyList<ImageAnalysisHiddenMessage> history) =>
        JsonSerializer.Serialize(new
        {
            messages = history.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            stream_options = new { include_usage = true }
        });

    public static async Task<string> ReadAsync(Stream stream, IProgress<ModelStreamChunk>? progress,
        Action<string> rawLine, CancellationToken token)
    {
        var text = new StringBuilder();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            rawLine(line);
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") return text.ToString();
            if (data.Length == 0) continue;
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;
            foreach (var field in new[] { "reasoning_content", "content" })
                if (delta.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } chunk)
                {
                    text.Append(chunk);
                    progress?.Report(new ModelStreamChunk(chunk));
                }
        }
        throw new EndOfStreamException("Chat stream disconnected before [DONE].");
    }
}
