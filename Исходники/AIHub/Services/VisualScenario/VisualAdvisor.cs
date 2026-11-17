using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VisualScenario;

internal sealed record VisualAdvice(string Reply, string[] Fragments);
internal sealed record VisualPreparedAdvisor(DebugModelInfo Model, bool Gemma, int Capacity)
{
    private int SafetyTokens => Gemma ? LetItOutChatRuntime.ContextSafetyTokens : LiteraryAutomaticBudget.SafetyTokens;

    internal StudioContextMeter Measure(int input) => new(input, Capacity) { SafetyTokens = SafetyTokens };

    internal int ReplyTokens(int input)
    {
        var available = Capacity - (long)input - SafetyTokens;
        if (available < LiteraryAutomaticBudget.MinimumReply)
            throw new InvalidOperationException("Visual.ContextFull");
        return (int)available;
    }
}

internal sealed class VisualAdvisor
{
    internal const string ChatKind = "visual-scenario.advisor";
    internal const string VramRecovery = "Visual.AdvisorVramRecovery";

    internal static bool RequiresCpuRecovery(string? detail) =>
        detail is VramRecovery or "LetItOut.VramChanged" or nameof(LetItOutMemoryException);

    private readonly LlamaServerRuntimeService _text = new(new UserContextService(new UserProfileStore(), new IpLocationService()));
    private readonly LlamaServerRuntimeService _vision = new(new UserContextService(new UserProfileStore(), new IpLocationService()));
    private string? _activeText;

    internal VisualAdvisor()
    {
        _text.ConfigurePrivateNoRetry();
        _vision.ConfigurePrivateNoRetry();
        _vision.ConfigureLetItOut((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(LlamaGpuDecision.Hybrid);
        }, null, visionGpuAllowance: LetItOutMemoryPolicy.VramReserve);
    }

    internal async Task<VisualPreparedAdvisor> PrepareAsync(string path, bool gemma, bool images, CancellationToken token, bool forceCpu = false)
    {
        var model = new DebugModelInfo
        {
            Name = Path.GetFileNameWithoutExtension(path), Path = path, Format = "gguf", SizeBytes = new FileInfo(path).Length,
            Role = gemma ? "let_it_out" : "visual_advisor"
        };
        var runtime = gemma ? _vision : _text;
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(runtime);
        if (gemma)
        {
            try
            {
                if (images || !runtime.IsRunningModel(path))
                    await runtime.SetLetItOutProjectorAsync(images ? await LetItOutVision.VerifiedPathAsync(path, token) : null, token);
                await runtime.WarmLetItOutAsync(model, token, forceCpu);
            }
            catch (LetItOutMemoryException error) { throw await RetireAfterMemoryFailureAsync(error); }
        }
        else if (_activeText != path || runtime.PoetrySession is null)
        {
            await runtime.PreparePoetryAsync(model, token, reasoningOptions: MusicPoetryReasoningOptions.Disabled,
                adaptivePlacement: true, requireFullPrompt: true);
            _activeText = path;
        }
        return new(model, gemma, await runtime.LetItOutEffectiveCapacityAsync(token));
    }

    internal async Task<string> CompleteAsync(VisualPreparedAdvisor prepared, IReadOnlyList<object> messages,
        CancellationToken token, bool structured = false)
    {
        var used = await CountAsync(prepared, messages, token);
        var replyTokens = prepared.ReplyTokens(used);
        var json = Payload(messages, replyTokens, structured);
        if (prepared.Gemma)
        {
            try
            {
                return LetItOutChatRuntime.ValidateResult(await _vision.GenerateLetItOutAsync(prepared.Model, json, replyTokens,
                    new Progress<ModelStreamChunk>(), token));
            }
            catch (LetItOutMemoryException error) { throw await RetireAfterMemoryFailureAsync(error); }
        }
        var reply = await _text.GeneratePoetryStreamAsync(prepared.Model, json, _ => { }, token);
        if (reply.FinishReason != "stop" || string.IsNullOrWhiteSpace(reply.Content))
            throw new InvalidDataException("Visual.AdvisorIncomplete");
        return reply.Content.Trim();
    }

    private async Task<BackgroundOperationWaitingException> RetireAfterMemoryFailureAsync(LetItOutMemoryException error)
    {
        await _vision.RetireLetItOutAsync();
        return new(error.Message == "LetItOut.VramChanged" ? VramRecovery : error.Message);
    }

    internal Task<int> CountAsync(VisualPreparedAdvisor prepared, IReadOnlyList<object> messages, CancellationToken token) =>
        prepared.Gemma ? _vision.CountLetItOutPromptAsync(Payload(messages), token)
            : _text.CountPrivatePromptAsync(Payload(messages), token);

    private static string Payload(IReadOnlyList<object> messages,
        int replyTokens = LiteraryAutomaticBudget.MinimumReply, bool structured = false)
    {
        var fields = new Dictionary<string, object>
        {
            ["messages"] = messages, ["stream"] = true, ["max_tokens"] = replyTokens, ["temperature"] = .65,
            ["chat_template_kwargs"] = new { enable_thinking = false }
        };
        if (structured) fields["response_format"] = new { type = "json_object" };
        return JsonSerializer.Serialize(fields);
    }

    internal static object Message(string role, string text) => new { role, content = text };
    internal static object ImageMessage(string text, IEnumerable<string> dataUrls) => new
    {
        role = "user", content = new[] { (object)new { type = "text", text } }
            .Concat(dataUrls.Select(url => (object)new { type = "image_url", image_url = new { url } })).ToArray()
    };

    internal static async Task<string> ImageUrlAsync(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Visual.MissingImage");
        var payload = await new VisionImagePayloadService().PrepareAsync(new()
        {
            Id = Guid.NewGuid().ToString("N"), SourcePath = path, DisplayName = file.Name,
            Extension = file.Extension, Category = SessionFileCategories.Image, IsAvailable = file.Exists, SizeBytes = file.Length
        }, token);
        return "data:" + payload.MimeType + ";base64," + Convert.ToBase64String(payload.Bytes);
    }

    internal static VisualAdvice ParseAdvice(string raw)
    {
        // A model may choose normal conversation with no suggestions. Preserve that reply without
        // treating its ordinary prose as an approved prompt fragment.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var json = JsonDocument.Parse(raw[start..(end + 1)]);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.String)
                {
                    var fragments = json.RootElement.TryGetProperty("fragments", out var items) && items.ValueKind == JsonValueKind.Array
                        ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!.Trim()).Where(text => text.Length > 0).ToArray() : [];
                    return new(reply.GetString()!.Trim(), fragments);
                }
            }
            catch (JsonException) { }
        }
        return new(raw.Trim(), []);
    }

    internal static VisualPrompt ParsePrompt(string raw, VisualInput[] images)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var document = JsonDocument.Parse(raw[start..(end + 1)]);
                var value = document.RootElement;
                if (value.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String
                    && prompt.GetString()?.Trim() is { Length: > 0 } text)
                {
                    if (!value.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array
                        || roles.GetArrayLength() != images.Length) return new(text, images);
                    return new(text, images.Select((image, i) => !image.IsBase && roles[i].ValueKind == JsonValueKind.String
                        && roles[i].GetString()?.Trim() is { Length: > 0 and <= 80 } role ? image with { Role = role } : image).ToArray());
                }
            }
            catch (JsonException) { }
        }
        return new(raw.Trim(), images);
    }

    internal static VisualPrompt PreparePrompt(string raw, VisualInput[] images, IReadOnlyList<string> manualLines)
    {
        var prompt = ParsePrompt(raw, images);
        var missing = manualLines.Select(line => line.Trim()).Distinct(StringComparer.Ordinal)
            .Where(line => line.Length > 0 && !prompt.Text.Contains(line, StringComparison.Ordinal)).ToArray();
        return missing.Length == 0 ? prompt : prompt with { Text = string.Join("\n", missing) + "\n\n" + prompt.Text };
    }
}
