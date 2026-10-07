using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    private readonly SemaphoreSlim _completionGate = new(1, 1);
    private Task _stderrPumpTask = Task.CompletedTask;

    private Task<StructuredChatResult> CompleteAsync(DebugModelInfo model, ChatCompletionRequest request,
        Action<string> log, IProgress<ModelStreamChunk>? progress, CancellationToken token) =>
        CompletePayloadAsync(model, JsonSerializer.Serialize(request, _jsonOptions), async (stream, tracked, cancellation) =>
        {
            if (request.Stream) return await OpenAiSseStreamParser.ReadAsync(stream, tracked, cancellation);
            var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(stream, _jsonOptions, cancellation);
            var choice = completion?.Choices.FirstOrDefault();
            if (choice?.Message is null) throw new InvalidDataException("Model response did not contain a completion.");
            return new StructuredChatResult { Content = choice.Message.Content?.Trim() ?? string.Empty,
                FinishReason = choice.FinishReason ?? string.Empty, ToolCalls = choice.Message.ToolCalls ?? [] };
        }, log, progress, token);

    // Private consumers retain their exact payload, sampling policy and response usage checks.
    internal Task<string> GeneratePrivateJsonAsync(DebugModelInfo model, string json, CancellationToken token) =>
        CompletePayloadAsync(model, json, async (stream, _, cancellation) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync(cancellation);
        }, _ => { }, null, token);

    private async Task<T> CompletePayloadAsync<T>(DebugModelInfo model, string json,
        Func<Stream, IProgress<ModelStreamChunk>?, CancellationToken, Task<T>> readResponse,
        Action<string> log, IProgress<ModelStreamChunk>? progress, CancellationToken token)
    {
        await _completionGate.WaitAsync(token);
        try
        {
            await EnsureStartedAsync(model, log, token);
            var visibleOutput = false;
            var trackedProgress = progress is null ? null : new InferenceProgress(chunk =>
            {
                if (chunk.Text.Length != 0) visibleOutput = true;
                progress.Report(chunk);
            });
            return await NativeInferenceRecovery.ExecuteAsync(
                async () =>
                {
                    token.ThrowIfCancellationRequested();
                    _hardwareStartupFailure = false;
                    try
                    {
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/v1/chat/completions") { Content = content };
                        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                        await using var stream = await response.Content.ReadAsStreamAsync(token);
                        if (!response.IsSuccessStatusCode)
                        {
                            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                            var buffer = new char[16 * 1024];
                            var length = 0;
                            while (length < buffer.Length)
                            {
                                var read = await reader.ReadAsync(buffer.AsMemory(length), token);
                                if (read == 0) break;
                                length += read;
                            }
                            throw new HttpRequestException($"Model HTTP {(int)response.StatusCode}: {new string(buffer, 0, length)}",
                                null, response.StatusCode);
                        }
                        return await readResponse(stream, trackedProgress, token);
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException)
                    {
                        // A native crash may close HTTP before the stderr reader reaches its diagnostic.
                        if (!token.IsCancellationRequested && _process is { HasExited: true })
                        {
                            try { await _stderrPumpTask.WaitAsync(TimeSpan.FromSeconds(2), token); }
                            catch (TimeoutException) { log("Native diagnostics did not drain within the bounded deadline."); }
                        }
                        throw;
                    }
                },
                () => _selectedRuntime?.UsesGpu == true, () => _hardwareStartupFailure,
                () => visibleOutput, () => ModelProcessRetirement.StopAsync(_process, Stop),
                () => EnsureStartedAsync(model, log, token, forceCpuFallback: true), log, token);
        }
        finally { _completionGate.Release(); }
    }

    private sealed class InferenceProgress(Action<ModelStreamChunk> report) : IProgress<ModelStreamChunk>
    {
        public void Report(ModelStreamChunk value) => report(value);
    }
}
