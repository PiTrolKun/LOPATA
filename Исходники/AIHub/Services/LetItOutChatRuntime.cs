using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public interface ILetItOutChatRuntime
{
    Task<string> GenerateAsync(LetItOutChatRequest request, IProgress<ModelStreamChunk> progress, CancellationToken token);
    Task RetireAsync();
}

/// <summary>A private Gemma conversation with scoped local memory; no user profile or external retrieval.</summary>
public sealed partial class LetItOutChatRuntime(UserContextService userContext) : ILetItOutChatRuntime, ILetItOutChatResident, ILetItOutChatTitleRuntime, ILetItOutMemoryRuntime, IDisposable
{
    internal const int AnswerTokens = 1536;
    internal const int ContextTokens = 16384;
    private readonly LlamaServerRuntimeService _runtime = new(userContext);
    public Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? DecideGpu { get; set; }
    public Action<string>? ModeChanged { get; set; }
    public Action<string>? AttachmentStageChanged { get; set; }
    public Func<int>? AudioThreads { get; set; }
    private static readonly Lazy<string> SystemPrompt = new(() => {
        using var stream = typeof(LetItOutChatRuntime).Assembly.GetManifestResourceStream("AIHub.LetItOutSystem")
            ?? throw new InvalidOperationException("Missing scenario prompt.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    public async Task WarmAsync(string modelPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await LetItOutModel.VerifyForUseAsync(modelPath, token);
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        _runtime.ConfigureLetItOut(DecideGpu, ModeChanged);
        await _runtime.WarmLetItOutAsync(new DebugModelInfo { Name = LetItOutModel.DisplayName,
            Path = modelPath, IsCoreModel = false, Format = "gguf", Role = "chat" }, token);
    }

    public async Task<string> GenerateAsync(LetItOutChatRequest request, IProgress<ModelStreamChunk> progress, CancellationToken token)
    {
        _recallForTurn = [];
        token.ThrowIfCancellationRequested();
        if (!string.Equals(Path.GetFileName(request.CoreModelPath), LetItOutModel.FileName, StringComparison.OrdinalIgnoreCase))
            throw new LetItOutModelChangedException();
        await LetItOutModel.VerifyForUseAsync(request.CoreModelPath, token);
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        _runtime.ConfigureLetItOut(DecideGpu, ModeChanged);
        var model = new DebugModelInfo { Name = LetItOutModel.DisplayName,
            Path = request.CoreModelPath, IsCoreModel = false, Format = "gguf", Role = "chat" };
        try
        {
            if (MemoryStore is { } audioStore && audioStore.BoundAttachments(request.SessionId).Any(a =>
                a.Kind == "audio" && audioStore.AudioTranscript(request.SessionId, a.Id)?.Complete != true))
            {
                AttachmentStageChanged?.Invoke("LetItOut.Attachments.AudioWorking");
                await _runtime.RetireLetItOutAsync();
                await LetItOutAudioTranscription.RecognizePendingAsync(audioStore, request.SessionId,
                    AudioThreads?.Invoke() ?? Math.Clamp(Environment.ProcessorCount / 4, 1, 8), token);
                AttachmentStageChanged?.Invoke("LetItOut.Working");
            }
            var hasImages = MemoryStore?.BoundAttachments(request.SessionId).Any(a => a.Kind == "image") == true;
            await _runtime.SetLetItOutProjectorAsync(hasImages ? await LetItOutVision.VerifiedPathAsync(request.CoreModelPath, token) : null, token);
            await _runtime.WarmLetItOutAsync(model, token);
            var inputs = await PrepareAttachmentTurnAsync(request, model, token);
            var json = MemoryStore is null ? BuildPayload(inputs) : await PrepareMemoryAnswerAsync(inputs, model, token);
            var result = await _runtime.GenerateLetItOutAsync(model, json, AnswerTokens,
                CreateAnswerProgress(progress), token);
            var answer = ValidateResult(result);
            token.ThrowIfCancellationRequested();
            if (MemoryStore is { } recallStore)
            {
                var saved = recallStore.Load(request.SessionId);
                var ordinal = saved?.Messages.Select((m, i) => (m.Role, Ordinal: i)).LastOrDefault(m => m.Role == "user").Ordinal;
                recallStore.SaveRecallLinks(request.SessionId, ordinal is { } n ? n + 1 : request.Messages.Count, _recallForTurn);
            }
            return answer;
        }
        catch (OperationCanceledException) { throw; }
        catch (LetItOutChatContextException) { throw; }
        catch (LetItOutChatIncompleteException) { throw; }
        catch (LetItOutMemoryException) { throw; }
        catch (InvalidDataException error) when (error.Message.StartsWith("LetItOut.Attachments.", StringComparison.Ordinal)) { throw; }
        // Server error bodies can echo the prompt. Never pass those bodies to shared diagnostics.
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { throw new IOException("LetItOut.RuntimeFailed"); }
    }

    internal static void ValidateCorePath(string path, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(currentPath)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetFileName(path), LetItOutModel.FileName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LetItOut.CoreMissing");
    }

    internal static string BuildPayload(LetItOutChatRequest request) => BuildPayload(request.Messages);

    internal static string BuildPayload(IReadOnlyList<LetItOutChatMessage> history, string memory = "")
    {
        var system = SystemPrompt.Value + memory;
        var messages = new[] { new LetItOutChatMessage("system", system) }.Concat(history)
            .Select(message => new { role = message.Role, content = MessageContent(message) }).ToArray();
        return JsonSerializer.Serialize(new { messages, stream = true, max_tokens = AnswerTokens,
            temperature = 1.0, top_p = 0.95, top_k = 64, min_p = 0.0, repeat_penalty = 1.0,
            repeat_last_n = 64, presence_penalty = 0.0, frequency_penalty = 0.0, seed = 42,
            chat_template_kwargs = new { enable_thinking = false } });
    }

    private static object MessageContent(LetItOutChatMessage message) => message.ImageInputs is not { Count: > 0 } ? message.Content
        : new[] { (object)new { type = "text", text = message.Content } }.Concat(message.ImageInputs.Select(url =>
            (object)new { type = "image_url", image_url = new { url } })).ToArray();

    internal static string ValidateResult(StructuredChatResult result)
    {
        if (!string.Equals(result.FinishReason, "stop", StringComparison.Ordinal)
            || result.HasToolCalls || string.IsNullOrWhiteSpace(result.Content))
            throw new LetItOutChatIncompleteException();
        return CleanAnswer(result.Content);
    }

    internal static string CleanAnswer(string text)
    {
        var filter = new LlamaAnswerContentFilter();
        try
        {
            var answer = (filter.Append(text) + filter.Complete()).Trim();
            if (string.IsNullOrWhiteSpace(answer)) throw new LetItOutChatIncompleteException();
            return answer;
        }
        catch (InvalidDataException) { throw new LetItOutChatIncompleteException(); }
    }

    internal static string CleanPartial(string text) => new LlamaAnswerContentFilter().Append(text);

    internal static IProgress<ModelStreamChunk> CreateAnswerProgress(IProgress<ModelStreamChunk> progress) =>
        new AnswerProgress(progress);

    private sealed class AnswerProgress(IProgress<ModelStreamChunk> progress) : IProgress<ModelStreamChunk>
    {
        private readonly LlamaAnswerContentFilter _filter = new();
        private bool _hasAnswer;
        public void Report(ModelStreamChunk chunk)
        {
            string text;
            try { text = _filter.Append(chunk.Text) + (chunk.IsComplete ? _filter.Complete() : ""); }
            catch (InvalidDataException) { throw new LetItOutChatIncompleteException(); }
            _hasAnswer |= !string.IsNullOrWhiteSpace(text);
            if (chunk.IsComplete && !_hasAnswer) throw new LetItOutChatIncompleteException();
            if (text.Length != 0 || chunk.IsComplete) progress.Report(new(text, chunk.IsComplete));
        }
    }

    internal static int? ReadContextCapacity(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("default_generation_settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("n_ctx", out var context)
            && context.ValueKind == JsonValueKind.Number
            && context.TryGetInt32(out var capacity) && capacity > AnswerTokens + 512) return capacity;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return null;
        var smallest = int.MaxValue;
        foreach (var slot in value.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object || !slot.TryGetProperty("n_ctx", out var slotContext)
                || slotContext.ValueKind != JsonValueKind.Number || !slotContext.TryGetInt32(out var slotCapacity)
                || slotCapacity <= AnswerTokens + 512) return null;
            smallest = Math.Min(smallest, slotCapacity);
        }
        return smallest;
    }

    internal static void RequireContextSpace(int inputTokens, int outputTokens, int capacity)
    {
        if ((long)inputTokens + outputTokens + 512 >= capacity) throw new LetItOutChatContextException();
    }

    public Task RetireAsync() => _runtime.RetireLetItOutAsync();
    public void Dispose() => _runtime.Dispose();
}

public sealed partial class LlamaServerRuntimeService
{
    internal async Task WarmLetItOutAsync(DebugModelInfo model, CancellationToken token)
    {
        await PrepareAsync(model, _ => { }, token);
        _ = await ReadLetItOutCapacityAsync(token);
        CheckLetItOutReserve();
    }

    // Keep token counting and completion on the same runtime, using the exact serialized messages.
    internal async Task<StructuredChatResult> GenerateLetItOutAsync(DebugModelInfo model, string json,
        int outputTokens, IProgress<ModelStreamChunk> progress, CancellationToken token)
    {
        await PrepareAsync(model, _ => { }, token);
        var capacity = await ReadLetItOutCapacityAsync(token);
        using var payload = JsonDocument.Parse(json);
        using var template = new StringContent(JsonSerializer.Serialize(new {
            messages = payload.RootElement.GetProperty("messages"), add_generation_prompt = true,
            chat_template_kwargs = payload.RootElement.GetProperty("chat_template_kwargs")
        }), Encoding.UTF8, "application/json");
        using var applied = await _httpClient.PostAsync(Endpoint + "/apply-template", template, token);
        applied.EnsureSuccessStatusCode();
        using var prompt = JsonDocument.Parse(await applied.Content.ReadAsStringAsync(token));
        using var input = new StringContent(JsonSerializer.Serialize(new {
            content = prompt.RootElement.GetProperty("prompt").GetString(), add_special = true, parse_special = true
        }), Encoding.UTF8, "application/json");
        using var counted = await _httpClient.PostAsync(Endpoint + "/tokenize", input, token);
        counted.EnsureSuccessStatusCode();
        using var counts = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
        var tokens = counts.RootElement.GetProperty("tokens").GetArrayLength() + ImageTokenReserve(payload.RootElement);
        LetItOutChatRuntime.RequireContextSpace(tokens, outputTokens, Math.Min(capacity, RequestedContextTokens));
        try
        {
            return await GuardLetItOutMemoryAsync(ct => CompletePayloadAsync(model, json,
                (stream, tracked, cancellation) => OpenAiSseStreamParser.ReadAsync(stream, tracked, cancellation),
                _ => { }, progress, ct), token);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest
            && error.Message.Contains("exceed_context_size_error", StringComparison.Ordinal))
        { throw new LetItOutChatContextException(); }
    }

    private async Task<int> ReadLetItOutCapacityAsync(CancellationToken token)
    {
        // Try the properties contract and the existing slot contract. Never substitute the requested capacity
        // when the running backend cannot confirm its actual slot size.
        foreach (var route in new[] { "/props", "/slots" })
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await _httpClient.GetAsync(Endpoint + route, deadline.Token);
                if (!response.IsSuccessStatusCode) continue;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                if (LetItOutChatRuntime.ReadContextCapacity(document.RootElement) is { } capacity) return capacity;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or JsonException) { }
        }
        token.ThrowIfCancellationRequested();
        throw new IOException("LetItOut.CapacityUnavailable");
    }

    internal async Task RetireLetItOutAsync()
    {
        await ModelProcessRetirement.StopAsync(_process, Stop);
        await Task.WhenAll(_stderrPumpTask, _stdoutPumpTask);
    }
}
