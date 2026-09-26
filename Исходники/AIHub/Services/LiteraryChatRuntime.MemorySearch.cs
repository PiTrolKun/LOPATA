using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    // These helpers run under StructuredAnalysisAsync's queue lease. Never acquire it recursively.
    private async Task<int> MemoryInputTokensAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages,
        CancellationToken token, bool thinking = true)
    {
        await PrepareAsync(token).ConfigureAwait(false);
        using var template = await PostJsonAsync("apply-template", new
        {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            add_generation_prompt = true, chat_template_kwargs = LiteraryModelPolicy.Thinking(thinking)
        }, token).ConfigureAwait(false);
        return await TokenCountAsync(template.RootElement.GetProperty("prompt").GetString()!, true, token).ConfigureAwait(false);
    }

    private async Task<bool> MemoryMessagesFitAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages, CancellationToken token)
    {
        var count = await MemoryInputTokensAsync(messages, token).ConfigureAwait(false);
        var reserve = ContextCapacity >= 8192 ? 1024 : 512;
        return (long)count + LiteraryAutomaticBudget.SafetyTokens + reserve <= ContextCapacity;
    }

    private async Task<string> ReadMemoryPassAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages, CancellationToken token)
    {
        await PrepareAsync(token).ConfigureAwait(false);
        var count = await MemoryInputTokensAsync(messages, token, thinking: false).ConfigureAwait(false);
        var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Advisor, messages))!.AsObject();
        body["max_tokens"] = Math.Min(2048, await AvailableReplyAsync(count, token).ConfigureAwait(false));
        body["temperature"] = 0;
        body["chat_template_kwargs"] = System.Text.Json.JsonSerializer.SerializeToNode(LiteraryModelPolicy.Thinking(false));
        body["response_format"] = new JsonObject { ["type"] = "json_object" };
        _diagnostics?.Write("memory_pass_request", new { count, context = ContextCapacity, body });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var answer = await LiteraryLoopStream.ReadAsync(stream, null,
                line => _diagnostics?.Write("memory_pass_sse", line), token).ConfigureAwait(false);
            _diagnostics?.Write("memory_pass_answer", answer);
            return LiteraryStructuredReply.Json(answer);
        }
        finally { await AwaitIdleAsync().ConfigureAwait(false); }
    }
}
