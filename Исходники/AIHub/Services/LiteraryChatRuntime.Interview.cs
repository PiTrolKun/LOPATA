using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public Task<string> InterviewAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages,
        Action<int> budget, CancellationToken token, JsonNode? responseSchema = null)
        => StructuredAnalysisAsync(messages, budget, token, responseSchema, "Interview");

    private async Task<string> StructuredAnalysisAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages,
        Action<int> budget, CancellationToken token, JsonNode? responseSchema, string role,
        object? snapshot = null, Action<string>? validate = null,
        Func<CancellationToken,Task<IReadOnlyList<ImageAnalysisHiddenMessage>>>? prepareMessages = null,
        LiteraryChatProfile profile = LiteraryChatProfile.Advisor, IProgress<ModelStreamChunk>? streamProgress = null, Action? attemptStarting = null,
        string? grammar = null, bool? reasoning = null)
    {
        var diagnosticsFolder = _layout?.EnsureFolder("Diagnostics/LiteraryDetailed")
            ?? System.IO.Path.Combine(_preparationRoot ?? AppDataPaths.BaseDirectory, "Diagnostics/LiteraryDetailed");
        using var queued = QueueRequest(token);
        await _gate.WaitAsync(queued.Token);
        BeginBudgetOperation();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        using var diagnostics = new LiteraryRequestDiagnostics(role, Log, diagnosticsFolder);
        _diagnostics = diagnostics; var requested = false;
        try
        {
            var ct = active.Token;
            diagnostics.Write("analysis_snapshot", snapshot);
            ValidatePreparation();
            _layout?.EnsurePresent();
            if (prepareMessages is not null) messages = await prepareMessages(ct).ConfigureAwait(false);
            await PrepareAsync(ct).ConfigureAwait(false);
            diagnostics.Watch(_process!);
            var thinking = reasoning ?? (snapshot is ParagraphRequest && responseSchema is null);
            using var applied = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true,
                chat_template_kwargs = LiteraryModelPolicy.Thinking(thinking) }, ct);
            var count = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, ct);
            var replyTokens = await AvailableReplyAsync(count, ct);
            budget(count);
            diagnostics.Write(role == "Interview" ? "interview_budget" : "analysis_budget", new { count, reserve = replyTokens + LiteraryAutomaticBudget.SafetyTokens, context = ContextCapacity });
            return await LiteraryLoopRecovery.RunAsync(async recovery =>
            {
                await PrepareAsync(ct).ConfigureAwait(false);
                replyTokens = await AvailableReplyAsync(count, ct);
                attemptStarting?.Invoke();
                var body = JsonNode.Parse(LiteraryModelPolicy.Request(profile, messages, recovery))!.AsObject();
                body["max_tokens"] = replyTokens; body["temperature"] = profile == LiteraryChatProfile.Writer ? .8 : role == "Calibration" ? .2 : .4;
                body["chat_template_kwargs"] = System.Text.Json.JsonSerializer.SerializeToNode(LiteraryModelPolicy.Thinking(thinking));
                if (grammar is not null)
                {
                    // b9442 WORD = 1. Activate only after reasoning; the grammar consumes the trigger too.
                    body["grammar"] = grammar;
                    body["grammar_lazy"] = true;
                    body["grammar_triggers"] = new JsonArray(new JsonObject { ["type"] = 1, ["value"] = "</think>" });
                    body["preserved_tokens"] = new JsonArray("</think>");
                }
                if (prepareMessages is not null) body["cache_prompt"] = false;
                if (responseSchema is not null)
                    body["response_format"] = new JsonObject { ["type"] = "json_object", ["schema"] = responseSchema.DeepClone() };
                diagnostics.Write(role == "Interview" ? "interview_request" : "analysis_request", body);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
                { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                requested = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var answer = await LiteraryLoopStream.ReadAsync(stream, new DiagnosticProgress(streamProgress, diagnostics), line => diagnostics.Write("sse", line), ct);
                if (string.IsNullOrWhiteSpace(answer)) throw new System.IO.InvalidDataException("Empty model reply.");
                diagnostics.Write(role == "Interview" ? "interview_answer" : "analysis_answer", answer);
                if (responseSchema is not null) answer = LiteraryStructuredReply.Json(answer);
                validate?.Invoke(answer); return answer;
            }, async evidence => { diagnostics.Write("loop_recovery", evidence); await AwaitIdleAsync(); }, ct);
        }
        catch (Exception ex) { diagnostics.Write("failure", ex.ToString()); throw; }
        finally
        {
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally { _diagnostics = null; _active = null; Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke(); }
        }
    }
}
