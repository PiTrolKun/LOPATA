using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LiteraryImport;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public async Task<string> ImportAnalyzeAsync(IReadOnlyList<ImageAnalysisHiddenMessage> messages,
        ImportSession session, string step, int minimumReply, CancellationToken token)
    {
        using var queued = QueueRequest(token);
        await _gate.WaitAsync(queued.Token).ConfigureAwait(false);
        BeginBudgetOperation();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(queued.Token);
        active.CancelAfter(TimeSpan.FromMinutes(8));
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        var requested = false;
        try
        {
            var ct = active.Token;
            ValidatePreparation(); _layout?.EnsurePresent();
            await PrepareAsync(ct).ConfigureAwait(false);
            var backendHash = ImportSession.HashFile(LlamaBackendPaths.ServerExecutablePath);
            var fingerprint = ImportSession.Hash(LiteraryModelLocation.Sha256 + backendHash + "import-v2-json-object-t0.2");
            if (session.State.RuntimeFingerprint.Length > 0 && session.State.RuntimeFingerprint != fingerprint)
                throw new InvalidDataException("Literary.Import.RuntimeChanged");
            session.State.RuntimeFingerprint = fingerprint; session.Save();
            using var template = await PostJsonAsync("apply-template", new
            {
                messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true,
                chat_template_kwargs = LiteraryModelPolicy.Thinking(false)
            }, ct);
            var count = await TokenCountAsync(template.RootElement.GetProperty("prompt").GetString()!, true, ct);
            var available = await AvailableReplyAsync(count, ct);
            if (available < minimumReply) throw new ImageAnalysisContextExhaustedException("Import block exceeds the loaded context.");
            var requestJson = JsonSerializer.Serialize(new
            {
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                id_slot = LiteraryModelPolicy.Slot(LiteraryChatProfile.Advisor), max_tokens = Math.Min(available, Math.Max(2048, minimumReply)),
                temperature = .2, repeat_penalty = 1.05, cache_prompt = false, stream = true,
                response_format = new { type = "json_object" },
                top_k = 40, top_p = .95, min_p = .05, chat_template_kwargs = LiteraryModelPolicy.Thinking(false)
            });
            var parentSteps = new[] { "normalized-v2", "author-decision-ledger", "project-created" };
            var parents = parentSteps.Select(s => session.State.Artifacts.LastOrDefault(a => a.Step == s)?.Id).OfType<string>().ToArray();
            var requestRecord = session.Add(step + "/request", requestJson, "complete", parents);
            session.AddJson(step + "/settings", new { model = LiteraryModelLocation.FileName, modelSha256 = LiteraryModelLocation.Sha256,
                backendSha256 = backendHash, inputTokens = count,
                template = template.RootElement.Clone(), ContextCapacity, minimumReply, protocol = "import-v2", thinking = false }, requestRecord.Id);
            var raw = session.BeginRaw(step + "/raw-response", requestRecord.Id); var status = "partial";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
                { Content = new StringContent(requestJson, Encoding.UTF8, "application/json") };
                requested = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                session.AddJson(step + "/http", new { status = (int)response.StatusCode }, requestRecord.Id);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var capture = new ImportCaptureStream(input, raw.Stream);
                if (!response.IsSuccessStatusCode)
                { await capture.CopyToAsync(Stream.Null, ct); response.EnsureSuccessStatusCode(); }
                var answer = await LiteraryLoopStream.ReadAsync(capture, null, _ => { }, ct).ConfigureAwait(false);
                await capture.CopyToAsync(Stream.Null, ct);
                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Empty import answer.");
                session.Add(step + "/answer", answer, "complete", requestRecord.Id);
                status = "complete"; return answer;
            }
            catch (Exception ex) { session.Add(step + "/failure", ex.Message, "error", requestRecord.Id); throw; }
            finally
            {
                try { raw.Stream.Flush(true); } finally { raw.Stream.Dispose(); }
                session.Register(step + "/raw-response", raw.Name, status, requestRecord.Id);
            }
        }
        finally
        {
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally { _active = null; Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke(); }
        }
    }
}
