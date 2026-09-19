using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public async Task<ParagraphReply> StudioAsync(StudioRequest request, Func<string,string> l,
        Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk> progress, CancellationToken ct,
        Action? attemptStarting = null, Action? preparationStarted = null)
    {
        ParagraphEvidence? evidence = null; LiteraryParagraphCatalog? catalog = null; string? stamp = null;
        var transfer = request.Action == "Transfer";
        var raw = await StructuredAnalysisAsync([], budget, ct, transfer ? LiteraryParagraphPrompts.Schema : null,
            "Studio" + request.Base.Role, request, prepareMessages: async token =>
            {
                preparationStarted?.Invoke();
                var editor = request.Base.Editor;
                var project = LiteraryProjectStore.ReadProject(editor.Directory);
                stamp = LiteraryParagraphRevision.Capture(editor);
                catalog = new(project, editor, l);
                evidence = await new LiteraryParagraphSources(project, editor, catalog).ReadAsync(request.Base.Task,
                    request.Base.Selection, receipt, token);
                if (stamp != LiteraryParagraphRevision.Capture(editor)) throw new IOException("Project changed during source reading.");
                return LiteraryStudioPrompts.Build(request, evidence, catalog, project, l);
            }, profile: request.Base.Role, streamProgress: progress, attemptStarting: attemptStarting,
            grammar: request.Base.Role == LiteraryChatProfile.Writer ? LiteraryParagraphPrompts.SingleParagraphGrammar : null,
            reasoning: !transfer);
        if (stamp != LiteraryParagraphRevision.Capture(request.Base.Editor)) throw new IOException("Project changed during generation.");
        if (!transfer) return new(raw, [], evidence!);
        var parsed = LiteraryParagraphPrompts.ParseAdvisor(raw, catalog!);
        return new(parsed.Task, parsed.Recommendations, evidence!);
    }

    /// <summary>No system message, project lookup, diagnostics or durable session. Shares the inference queue only.</summary>
    public async Task<string> FreeChatAsync(IReadOnlyList<ImageAnalysisHiddenMessage> conversation,
        IProgress<ModelStreamChunk> progress, CancellationToken token, Action<int>? budget = null)
    {
        if (conversation.Any(m => m.Role is not ("user" or "assistant"))) throw new ArgumentException("Only conversation messages are allowed.");
        // Copy before waiting: later edits in another window must not change an enqueued request.
        var messages = conversation.Select(m => new ImageAnalysisHiddenMessage { Role = m.Role, Content = m.Content }).ToArray();
        using var queued = QueueRequest(token);
        await _gate.WaitAsync(queued.Token);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        _active = active; _diagnostics = null; _suppressBackendLog = true; _privateSlot = true; IsFreeChatBusy = true;
        Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke(); BeginBudgetOperation();
        var requested = false;
        try
        {
            var ct = active.Token;
            await PrepareAsync(ct).ConfigureAwait(false);
            using var applied = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                add_generation_prompt = true, chat_template_kwargs = LiteraryModelPolicy.Thinking() }, ct);
            var count = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, ct);
            var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Advisor, messages))!.AsObject();
            body["max_tokens"] = await AvailableReplyAsync(count, ct); body["cache_prompt"] = false; budget?.Invoke(count);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            requested = true;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await LiteraryLoopStream.ReadAsync(stream, progress, _ => { }, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally
            {
                // Keep backend output suppressed for this process lifetime, including asynchronously drained lines.
                IsFreeChatBusy = false; _active = null; Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke();
            }
        }
    }
}
