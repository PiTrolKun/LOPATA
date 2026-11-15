using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed record VideoStructuredAnswer(string Text, OutputModelUse Model, bool IsPartial = false);
internal sealed class VideoStructuredContextException(int input, int output, int capacity)
    : Exception("VideoPrep.AiContextLimit")
{
    internal int RequiredTokens => checked(input + output + 256);
    internal int Capacity => capacity;
}

public sealed partial class VideoConceptAssistant
{
    /// <summary>Tool-free structured generation using the resident model's audited placement and exact private messages.</summary>
    internal async Task<VideoStructuredAnswer> GenerateStructuredAsync(VideoConceptModelRequest model,
        string system, string input, object schema, int maxTokens, CancellationToken token, bool reportFit = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(input)
            || string.IsNullOrWhiteSpace(model.ModelPath) || maxTokens < 1 || !reportFit && maxTokens > VideoConceptModels.Context)
            throw new InvalidDataException("VideoPrep.AiInvalidRequest");
        using var audit = ComponentAudit.CaptureOperation();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!reportFit) deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var cancellation = deadline.Token;
        await _residentGate.WaitAsync(cancellation);
        try
        {
            await EnsureResidentAsync(model, cancellation);
            var payload = JsonSerializer.Serialize(new
            {
                model = "local",
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
                max_tokens = maxTokens, temperature = .35, top_p = .9, top_k = 40,
                min_p = 0, repeat_penalty = 1.05, seed = 42,
                stream = reportFit, cache_prompt = false,
                chat_template_kwargs = new { enable_thinking = false },
                reasoning_format = "deepseek", response_format = schema
            });
            if (reportFit) deadline.CancelAfter(TimeSpan.FromMinutes(2));
            int used;
            try { used = await _runtime.CountPrivatePromptAsync(payload, cancellation); }
            catch (Exception error) when (reportFit && IsPrivateTransportFailure(error))
            {
                await ResetExitedPrivateWorkerAsync();
                throw new IOException("SongClip.PlannerTransportLost", error);
            }
            // The caller owns the response schema and required reserve. Never cut
            // the input or reduce the output budget to make a partial plan appear valid.
            if ((long)used + maxTokens + 256 > _capacity)
            {
                OwnedProcessRegistry.Log("video_structured_context_limit", "VideoPreparation",
                    detail: $"capacity={_capacity};input={used};requiredOutput={maxTokens};headroom=256");
                if (reportFit) throw new VideoStructuredContextException(used, maxTokens, _capacity);
                throw new BackgroundOperationFailedException("VideoPrep.AiContextLimit");
            }
            if (reportFit)
            {
                // Prefill has no output chunks. Long prompts get a larger initial
                // allowance; afterwards only inactivity can end an active stream.
                var idle = TimeSpan.FromMinutes(2);
                deadline.CancelAfter(idle + TimeSpan.FromSeconds(used / 15d));
                StructuredChatResult answer;
                try { answer = await _runtime.GeneratePrivateStreamAsync(_model!, payload, () => deadline.CancelAfter(idle), cancellation); }
                catch (JsonException error) { throw new InvalidDataException("VideoPrep.AiInvalidReply", error); }
                catch (HttpRequestException error) when (error.Message.Contains("\"exceed_context_size_error\"", StringComparison.Ordinal))
                {
                    _capacity = Math.Min(_runtime.PoetrySession?.Context ?? _capacity, used + maxTokens + 255);
                    throw new VideoStructuredContextException(used, maxTokens, _capacity);
                }
                catch (Exception error) when (IsPrivateTransportFailure(error)
                    || error is IOException && error.Message == OpenAiSseStreamParser.PrematureEnd)
                {
                    await ResetExitedPrivateWorkerAsync();
                    throw new InvalidDataException("VideoPrep.AiIncomplete", error);
                }
                if (answer.ToolCalls.Count != 0 || string.IsNullOrWhiteSpace(answer.Content))
                    throw new InvalidDataException("VideoPrep.AiInvalidReply");
                if (answer.FinishReason is not ("stop" or "length" or "interrupted"))
                    throw new InvalidDataException("VideoPrep.AiIncomplete");
                return new(answer.Content, _baseIdentity! with { Role = "structured text generation" }, answer.FinishReason != "stop");
            }
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

    private static bool IsPrivateTransportFailure(Exception error) =>
        error is HttpIOException or HttpRequestException { StatusCode: null };

    private async Task ResetExitedPrivateWorkerAsync()
    {
        if (_runtime.PoetrySession is not null) return;
        await _runtime.RetirePoetryAsync();
        _residentKey = "";
    }
}
