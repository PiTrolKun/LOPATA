using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoStructuredAnswer(string Text, OutputModelUse Model);

public sealed partial class VideoConceptAssistant
{
    /// <summary>Tool-free structured generation using the resident model's audited placement and exact private messages.</summary>
    internal async Task<VideoStructuredAnswer> GenerateStructuredAsync(VideoConceptModelRequest model,
        string system, string input, object schema, int maxTokens, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(input)
            || string.IsNullOrWhiteSpace(model.ModelPath) || maxTokens is < 1 or > VideoConceptModels.Context)
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        using var audit = ComponentAudit.CaptureOperation();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var cancellation = deadline.Token;
        await _residentGate.WaitAsync(cancellation);
        try
        {
            await EnsureResidentAsync(model, cancellation);
            var used = await _runtime.CountPoetryAsync(system,
                [new("user", input, Guid.NewGuid().ToString("N"))], cancellation);
            // The caller owns the response schema and required reserve. Never cut
            // the input or reduce the output budget to make a partial plan appear valid.
            if ((long)used + maxTokens + 256 > _capacity)
            {
                OwnedProcessRegistry.Log("video_structured_context_limit", "VideoPreparation",
                    detail: $"capacity={_capacity};input={used};requiredOutput={maxTokens};headroom=256");
                throw new BackgroundOperationFailedException("VideoPrep.AiContextLimit");
            }
            var payload = JsonSerializer.Serialize(new
            {
                model = "local",
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
                max_tokens = maxTokens, temperature = .35, top_p = .9, top_k = 40,
                min_p = 0, repeat_penalty = 1.05, seed = 42,
                stream = false, cache_prompt = false,
                chat_template_kwargs = new { enable_thinking = false },
                reasoning_format = "deepseek", response_format = schema
            });
            var response = await _runtime.GeneratePrivateJsonAsync(_model!, payload, cancellation);
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(response);
                var choice = document.RootElement.GetProperty("choices")[0];
                if (choice.GetProperty("finish_reason").GetString() != "stop")
                    throw new InvalidDataException("VideoPrep.AiIncomplete");
                var message = choice.GetProperty("message");
                if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null
                        && (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() != 0)
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(content.GetString()))
                    throw new InvalidDataException("VideoPrep.AiInvalidReply");
                return new(content.GetString()!, _baseIdentity! with { Role = "structured text generation" });
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            { throw new InvalidDataException("VideoPrep.AiInvalidReply", error); }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        { throw new TimeoutException("VideoPrep.AiTimeout"); }
        finally
        {
            try { if (cancellation.IsCancellationRequested) await _runtime.RetirePoetryAsync(); }
            finally { _residentGate.Release(); RuntimeChanged?.Invoke(); }
        }
    }
}
