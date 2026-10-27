using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    internal Task<string> GeneratePrivateStreamJsonAsync(DebugModelInfo model, string json, Action<string>? reasoning, CancellationToken token) =>
        CompletePayloadAsync(model, json, (stream, tracked, cancellation) => ReadPrivateStreamAsync(stream, tracked, reasoning, cancellation),
            message => _poetryJournal?.Record("inference", message), new InferenceProgress(_ => { }), token);
    internal static async Task<string> ReadPrivateStreamAsync(Stream stream, IProgress<ModelStreamChunk>? tracked, Action<string>? reasoning, CancellationToken cancellation)
    {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var content = new StringBuilder(); var thoughtCount = 0; var finish = ""; var done = false;
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (line.Length > 1_000_000) throw new InvalidDataException("VideoPrep.ShotsInvalidReply");
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim(); if (data == "[DONE]") { done = true; break; } if (data.Length == 0) continue;
                using var document = JsonDocument.Parse(data); var root = document.RootElement;
                if (root.TryGetProperty("error", out _)) throw new IOException("Private model stream returned an error.");
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0]; if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString()!;
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                foreach (var key in new[] { "reasoning_content", "reasoning" })
                    if (delta.TryGetProperty(key, out var thought) && thought.ValueKind == JsonValueKind.String)
                    {
                        var text = thought.GetString()!; tracked?.Report(new ModelStreamChunk(text));
                        if (thoughtCount < 32000) { var keep = text[..Math.Min(text.Length, 32000 - thoughtCount)]; thoughtCount += keep.Length; reasoning?.Invoke(keep); }
                    }
                if (delta.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String)
                { var text = value.GetString()!; content.Append(text); tracked?.Report(new ModelStreamChunk(text)); }
                if (content.Length > 200000) throw new InvalidDataException("VideoPrep.ShotsInvalidReply");
            }
            if (!done || finish != "stop") throw new InvalidDataException("VideoPrep.ShotsInvalidReply");
            return JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = content.ToString() } } } });
    }
}
