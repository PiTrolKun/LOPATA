using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public const int InterviewContext = LiteraryModelPolicy.AdvisorContext, InterviewReply = 1024;
    public async Task<string> InterviewAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages,
        Action<int> budget, CancellationToken token, JsonNode? responseSchema = null)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Another literary operation is active.");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        using var diagnostics = new LiteraryRequestDiagnostics("Interview", Log,
            _layout?.EnsureFolder("Diagnostics/LiteraryDetailed") ?? System.IO.Path.Combine(_preparationRoot!, "Diagnostics/LiteraryDetailed"));
        _diagnostics = diagnostics; var requested = false;
        try
        {
            var ct = active.Token;
            ValidatePreparation();
            await PrepareAsync(ct).ConfigureAwait(false);
            diagnostics.Watch(_process!);
            using var applied = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true }, ct);
            var count = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, ct);
            budget(count);
            diagnostics.Write("interview_budget", new { count, reserve = InterviewReply + LiteraryModelPolicy.SafetyTokens, context = InterviewContext });
            if (count + InterviewReply + LiteraryModelPolicy.SafetyTokens > InterviewContext)
                throw new ImageAnalysisContextExhaustedException("Interview context exhausted.");
            return await LiteraryLoopRecovery.RunAsync(async recovery =>
            {
                await PrepareAsync(ct).ConfigureAwait(false);
                var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Advisor, messages, recovery))!.AsObject();
                body["max_tokens"] = InterviewReply; body["temperature"] = .4;
                if (responseSchema is not null)
                    body["response_format"] = new JsonObject { ["type"] = "json_object", ["schema"] = responseSchema.DeepClone() };
                diagnostics.Write("interview_request", body);
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
                { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                requested = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var answer = await LiteraryLoopStream.ReadAsync(stream, new DiagnosticProgress(null, diagnostics), line => diagnostics.Write("sse", line), ct);
                if (string.IsNullOrWhiteSpace(answer)) throw new System.IO.InvalidDataException("Empty model reply.");
                diagnostics.Write("interview_answer", answer); return answer;
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
