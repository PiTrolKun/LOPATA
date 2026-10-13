using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record MusicPoetryStreamUpdate(string Text, bool ReasoningEnded = false);
internal sealed record MusicPoetryStreamResult(string Content, string FinishReason);

/// <summary>Real native channels only. Final JSON stays private until completion and validation.</summary>
internal static class MusicPoetryStreamParser
{
    internal static async Task<MusicPoetryStreamResult> ReadAsync(Stream stream,
        Action<MusicPoetryStreamUpdate> progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        var content = new StringBuilder(); var finish = "";
        var thoughtSeen = false; var answerStarted = false; var done = false;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { done = true; break; }
            if (data.Length == 0) continue;
            using var document = JsonDocument.Parse(data); var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new IOException("Music.Poetry.StreamFailed");
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                finish = reason.GetString() ?? "";
            if (!choice.TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("reasoning_content", out var thought) && thought.ValueKind == JsonValueKind.String
                && thought.GetString() is { Length: > 0 } text)
            {
                if (answerStarted) throw new InvalidDataException("Music.Poetry.StreamFailed");
                thoughtSeen = true; progress(new(text));
            }
            if (delta.TryGetProperty("content", out var final) && final.ValueKind == JsonValueKind.String
                && final.GetString() is { Length: > 0 } answer)
            {
                if (!answerStarted && thoughtSeen) progress(new("", true));
                answerStarted = true; content.Append(answer);
            }
        }
        token.ThrowIfCancellationRequested();
        if (!done || finish.Length == 0) throw new MusicPoetryIncompleteReply(content.ToString());
        return new(content.ToString(), finish);
    }
}
