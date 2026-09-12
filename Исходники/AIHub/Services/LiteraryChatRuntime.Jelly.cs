using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    public async Task<string> ExtractJellyAsync(string source, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Another literary operation is active.");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token); active.CancelAfter(TimeSpan.FromMinutes(3));
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        LiteraryRequestDiagnostics? diagnostics = null;
        var requested = false;
        try
        {
            diagnostics = new LiteraryRequestDiagnostics("JellyExtraction", Log, _layout?.EnsureFolder("Diagnostics/LiteraryDetailed"));
            _diagnostics = diagnostics;
            var ct = active.Token; _layout?.EnsurePresent();
            await PrepareAsync(ct).ConfigureAwait(false); diagnostics.Watch(_process!);
            var messages = new[] { new ImageAnalysisHiddenMessage { Role = "system", Content = LiteraryJellyContract.Instruction },
                new ImageAnalysisHiddenMessage { Role = "user", Content = source } };
            diagnostics.Write("jelly_source", new { revision = LiteraryWorkIndex.Revision(source), source });
            using var template = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true }, ct);
            var count = await TokenCountAsync(template.RootElement.GetProperty("prompt").GetString()!, true, ct);
            diagnostics.Write("budget", new { count, reply = 1600, context = 8192 });
            if (count + 1600 + LiteraryModelPolicy.SafetyTokens > 8192) throw new ImageAnalysisContextExhaustedException("Memory extraction context is too large.");
            var result = await LiteraryLoopRecovery.RunAsync(async recovery =>
            {
                await PrepareAsync(ct).ConfigureAwait(false);
                var body = JsonSerializer.Serialize(new { messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                    id_slot = LiteraryModelPolicy.Slot(LiteraryChatProfile.Writer), max_tokens = 1600, temperature = 0.1,
                    repeat_penalty = recovery ? 1.1 : 1.05, cache_prompt = false, stream = true,
                    response_format = new { type = "json_object" } });
                diagnostics.Write("request", new { endpoint = Server, json = body });
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                requested = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await LiteraryLoopStream.ReadAsync(stream, new DiagnosticProgress(null, diagnostics), line => diagnostics.Write("sse", line), ct).ConfigureAwait(false);
            }, async _ => { await AwaitIdleAsync(); }, ct);
            diagnostics.Write("jelly_raw", result); return result;
        }
        catch (Exception ex) { diagnostics?.Write("failure", new { type = ex.GetType().Name, ex.Message }); throw; }
        finally
        {
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally { _active = null; _diagnostics = null; diagnostics?.Dispose(); Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke(); }
        }
    }
}
