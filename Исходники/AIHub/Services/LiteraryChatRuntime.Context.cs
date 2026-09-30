using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime : ILiteraryContextTools
{
    private sealed record ContextEvidence(StudioRequest Request, ParagraphEvidence Evidence, string Revision);
    private ContextEvidence? _contextEvidence;
    public event Action<StudioContextMeter>? StudioContextMeasured;
    private void ObserveStudioContext(StudioRequest request, ParagraphEvidence evidence, string revision, int count)
    {
        _contextEvidence = new(request, evidence, revision);
        StudioContextMeasured?.Invoke(new(count, ContextCapacity));
    }
    public async Task<StudioContextMeter?> MeasureStudioContextAsync(StudioRequest request,
        Func<string, string> localize, CancellationToken token)
    {
        using var queued = QueueRequest(token);
        if (!await _gate.WaitAsync(0, queued.Token).ConfigureAwait(false)) return null;
        try
        {
            // Passive measurement never loads/restarts a model or launches source/model inference.
            if (_process is not { HasExited: false } || ContextCapacity == 0) return null;
            var saved = _contextEvidence;
            var prepared = await Task.Run(() =>
            {
                var project = LiteraryProjectStore.ReadProject(request.Base.Editor.Directory);
                var catalog = new LiteraryParagraphCatalog(project, request.Base.Editor, localize);
                var valid = saved is not null && saved.Revision == LiteraryParagraphRevision.Capture(request.Base.Editor)
                    && ParagraphJson.Encode(saved.Request.Base.Selection) == ParagraphJson.Encode(request.Base.Selection);
                var evidence = valid ? saved!.Evidence : new ParagraphEvidence([], []);
                var messages = LiteraryStudioPrompts.Build(request, evidence, catalog, project, localize);
                var estimate = !valid || saved!.Request.Base.Task != request.Base.Task
                    || saved.Request.PreviousTask != request.PreviousTask;
                return (messages, estimate);
            }, queued.Token).ConfigureAwait(false);
            using var applied = await PostJsonAsync("apply-template", new
            {
                messages = prepared.messages.Select(m => new { role = m.Role, content = m.Content }),
                add_generation_prompt = true, chat_template_kwargs = LiteraryModelPolicy.Thinking()
            }, queued.Token).ConfigureAwait(false);
            var count = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, queued.Token).ConfigureAwait(false);
            return new(count, ContextCapacity, prepared.estimate);
        }
        finally { _gate.Release(); }
    }
    public async Task<StudioCompactionResult> CompactStudioContextAsync(StudioContextPlan plan,
        StudioContextMethod method, double ratio, IProgress<StudioCompactionProgress> progress, CancellationToken token)
    {
        using var queued = QueueRequest(token);
        await _gate.WaitAsync(queued.Token).ConfigureAwait(false);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        using var diagnostics = new LiteraryRequestDiagnostics("ContextCompaction", Log,
            _layout?.EnsureFolder("Diagnostics/LiteraryDetailed") ?? Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics/LiteraryDetailed"));
        _diagnostics = diagnostics;
        try
        {
            BeginBudgetOperation();
            await PrepareAsync(active.Token).ConfigureAwait(false);
            diagnostics.Watch(_process!); diagnostics.Write("context_compaction_snapshot", new { plan, method, ratio });
            var engine = new LiteraryContextCompaction(
                (text, ct) => TokenCountAsync(text, false, ct),
                (messages, ct) => MemoryInputTokensAsync(messages, ct, thinking: false),
                ReadContextCompactionPassAsync, () => ContextCapacity);
            var result = await engine.RunAsync(plan, method, ratio, progress, active.Token).ConfigureAwait(false);
            diagnostics.Write("context_compaction_draft", result); return result;
        }
        catch (Exception ex) { diagnostics.Write("failure", ex.ToString()); throw; }
        finally
        {
            try { await AwaitIdleAsync().ConfigureAwait(false); }
            finally { EndBudgetOperation(); _diagnostics = null; _active = null; Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke(); }
        }
    }
    private async Task<string> ReadContextCompactionPassAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages, CancellationToken token)
    {
        var count = await MemoryInputTokensAsync(messages, token, thinking: false).ConfigureAwait(false);
        var body = JsonNode.Parse(LiteraryModelPolicy.Request(LiteraryChatProfile.Advisor, messages))!.AsObject();
        body["max_tokens"] = await AvailableReplyAsync(count, token);
        body["temperature"] = .4; body["cache_prompt"] = false;
        body["chat_template_kwargs"] = System.Text.Json.JsonSerializer.SerializeToNode(LiteraryModelPolicy.Thinking(false));
        _diagnostics?.Write("context_compaction_pass_request", new { count, context = ContextCapacity, body });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var answer = await LiteraryLoopStream.ReadAsync(stream, null,
                line => _diagnostics?.Write("context_compaction_sse", line), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Empty context retelling.");
            _diagnostics?.Write("context_compaction_pass_answer", answer); return answer;
        }
        finally { await AwaitIdleAsync().ConfigureAwait(false); }
    }
}
