using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public static class OmniLlamaProtocol
{
    // A normal 32K conversation, one slot. No automatic shrinking to the test PC's free memory.
    public const int ContextTokens = 32768;
    public const double Temperature = 0.6;
    public const double TopP = 0.95;
    public const int TopK = 20;

    public static string DeviceMapJson => DescribeDeviceMap(OmniLlamaProfile.Alpha);

    public static string DescribeDeviceMap(OmniLlamaProfile profile) => JsonSerializer.Serialize(new
    {
        model = "cuda", projector = "cuda", quantization = profile.Quantization, projectorQuantization = profile.ProjectorQuantization,
        sourceModel = profile.SourceModel, modelRevision = profile.Revision,
        contextTokens = ContextTokens, slots = 1, fit = false,
        temperature = profile.Temperature, topP = TopP, topK = TopK, minP = 0, reasoning = true
    });

    public static string[] Arguments(string model, string projector, int port) =>
    ["-m", model, "--mmproj", projector, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-c", ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture), "-np", "1", "-ngl", "99",
        "--mmproj-offload", "--fit", "off", "--no-context-shift", "--offline", "--jinja",
        "--reasoning-format", "deepseek", "-fa", "auto", "-n", "-1",
        "--image-max-tokens", OmniContextBudget.ImageTokenUpperBound.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    public static string BuildRequest(IReadOnlyList<ImageAnalysisHiddenMessage> conversation, string imageDataUrl, int maxTokens = -1, OmniLlamaProfile? profile = null, string? command = null)
    {
        if (conversation.Count == 0 || conversation.Any(m => m.Role is not ("user" or "assistant")))
            throw new InvalidDataException("Invalid Omni conversation roles.");
        var messages = new JsonArray();
        foreach (var message in conversation)
        {
            JsonNode content = JsonValue.Create(message.Content)!;
            if (message.IncludesImage)
            {
                if (message.Role != "user" || !imageDataUrl.StartsWith("data:image/", StringComparison.Ordinal))
                    throw new InvalidDataException("An image-bearing user message requires a local image.");
                content = new JsonArray(
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = imageDataUrl } },
                    new JsonObject { ["type"] = "text", ["text"] = message.Content });
            }
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = content });
        }
        var templateOptions = new JsonObject { ["enable_thinking"] = true };
        // Batch formatting has no image history; single-image composition retains its image.
        if (profile == OmniLlamaProfile.Gamma && command == "compose" && !conversation.Any(m => m.IncludesImage))
            templateOptions["reasoning_effort"] = "medium";
        return new JsonObject
        {
            ["messages"] = messages, ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = (profile ?? OmniLlamaProfile.Alpha).Temperature, ["top_p"] = TopP, ["top_k"] = TopK, ["min_p"] = 0,
            ["chat_template_kwargs"] = templateOptions,
            ["max_tokens"] = maxTokens, ["cache_prompt"] = true
        }.ToJsonString();
    }

    public static async Task<OmniTextGenerationResult> ReadAsync(Stream stream,
        IProgress<ModelStreamChunk>? progress, Action<string>? saveRaw, CancellationToken cancellationToken, OmniLlamaProfile? profile = null,
        Action<string>? onRawLine = null)
    {
        var timer = Stopwatch.StartNew();
        var raw = new StringBuilder();
        var text = new StringBuilder();
        var answer = new LlamaAnswerContentFilter();
        var finish = string.Empty;
        var inputTokens = 0;
        var outputTokens = 0;
        long firstTokenMs = 0;
        var receivedToken = false;
        var done = false;
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                raw.AppendLine(line);
                onRawLine?.Invoke(line);
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") { done = true; break; }
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error)) throw new InvalidDataException(error.ToString());
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var p)) inputTokens = p.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var c)) outputTokens = c.GetInt32();
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String)
                    finish = f.GetString() ?? string.Empty;
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                var hasReasoning = delta.TryGetProperty("reasoning_content", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(reasoning.GetString());
                var hasContent = delta.TryGetProperty("content", out var value)
                    && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString());
                if (!receivedToken && (hasReasoning || hasContent))
                {
                    firstTokenMs = timer.ElapsedMilliseconds;
                    receivedToken = true;
                }
                if (hasContent
                    && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } chunk)
                {
                    var visible = answer.Append(chunk);
                    if (visible.Length > 0)
                    {
                        text.Append(visible);
                        progress?.Report(new ModelStreamChunk(visible));
                    }
                }
            }
        }
        finally { saveRaw?.Invoke(raw.ToString()); }
        if (finish == "length") throw new ImageAnalysisContextExhaustedException("The Omni response reached the safe context budget. Start a new session.", outputTruncated: true);
        var tail = answer.Complete();
        text.Append(tail);
        if (tail.Length > 0) progress?.Report(new ModelStreamChunk(tail));
        if (done && finish == "stop" && string.IsNullOrWhiteSpace(text.ToString()))
            throw new ImageAnalysisOmniFormatException(new InvalidDataException("The completed Omni response has no final answer."));
        if (!done || finish != "stop" || string.IsNullOrWhiteSpace(text.ToString()))
            throw new InvalidDataException($"Incomplete Omni response: finish={finish}; done={done}; chars={text.Length}.");
        progress?.Report(new ModelStreamChunk(string.Empty, true));
        var decodeMs = Math.Max(1, timer.ElapsedMilliseconds - firstTokenMs);
        return new(text.ToString().Trim(), timer.ElapsedMilliseconds, inputTokens, outputTokens, ContextTokens,
            "eos", firstTokenMs, decodeMs, firstTokenMs, outputTokens * 1000.0 / decodeMs,
            0, (profile ?? OmniLlamaProfile.Alpha).DiagnosticProfile, "llama.cpp-auto", RawProtocol: raw.ToString());
    }
}
