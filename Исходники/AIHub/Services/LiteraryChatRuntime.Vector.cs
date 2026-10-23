using System.IO;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed record VectorChatReply(string SvgAnswer, string EngineSet, string Backend, string Revision);

public sealed partial class LiteraryChatRuntime
{
    /// <summary>Vector text generation shares the verified model lifecycle, without prose loop detection or an output cap.</summary>
    public async Task<VectorChatReply> VectorChatAsync(string prompt, IProgress<ModelStreamChunk>? progress, CancellationToken token,
        IProgress<ModelStreamChunk>? reasoningProgress = null)
    {
        using var audit = ComponentAudit.CaptureOperation();
        using var queued = QueueRequest(token);
        await _gate.WaitAsync(queued.Token);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        _active = active; _diagnostics = null; _suppressBackendLog = true; _privateSlot = true;
        Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke(); BeginBudgetOperation(); var requested = false;
        try
        {
            var ct = active.Token; await PrepareAsync(ct).ConfigureAwait(false);
            var messages = new[] { new ImageAnalysisHiddenMessage { Role = "user", Content = prompt + "\n\nВерни один полный самодостаточный статический SVG с xmlns и viewBox. Без Markdown, пояснений, внешних ресурсов, растровых изображений, CSS-блоков, скриптов, анимации и use. Допустимы примитивы, path, текст, градиенты, clipPath, маски и статические SVG-фильтры. Заверши файл закрывающим тегом svg." } };
            using var applied = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true, chat_template_kwargs = LiteraryModelPolicy.Thinking() }, ct);
            var count = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, ct);
            _ = await AvailableReplyAsync(count, ct); // Physical context and memory check; no user output quota.
            var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Writer, messages))!.AsObject();
            body["max_tokens"] = -1;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions")) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            requested = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var answer = await LiteraryRawProtocol.ReadAsync(stream, progress, _ => { }, ct, requireComplete: true,
                includeReasoning: false, reasoningProgress: reasoningProgress).ConfigureAwait(false);
            return new(answer, _selectedRuntime!.Bundle.EngineSetId, _selectedRuntime.Bundle.Backend, _selectedRuntime.Bundle.Revision);
        }
        finally
        {
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally { _active = null; Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke(); }
        }
    }
}
