using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>One workspace owns one model process. Two slots, one active generation.</summary>
public sealed class LiteraryChatRuntime : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _port, _busy;
    private CancellationTokenSource? _active;
    private readonly object _logGate = new();
    private readonly string _logPath;
    private LiteraryRequestDiagnostics? _diagnostics;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public event Action? BusyChanged;
    public LiteraryChatRuntime()
    {
        var folder = Path.Combine(AppDataPaths.BaseDirectory, "Diagnostics", "LiteraryShared");
        Directory.CreateDirectory(folder);
        _logPath = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N") + ".log");
    }
    public static string[] Arguments(string model, int port) =>
    ["-m", model, "--host", IPAddress.Loopback.ToString(), "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-c", LiteraryModelPolicy.SharedContext.ToString(), "-np", "2", "-kvu", "--no-cache-idle-slots",
        "-ngl", "99", "--device", "CUDA0", "--fit", "off", "--cache-ram", "0", "--no-context-shift",
        "--offline", "--jinja", "--slots", "-cb", "-n", "-1",
        "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "-tb", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString()];
    private Uri Server => new($"http://{IPAddress.Loopback}:{_port}/");

    public async Task<string> SendAsync(LiteraryChatProfile role, IReadOnlyList<ImageAnalysisHiddenMessage> history,
        string draft, LiteraryProject project, IProgress<ModelStreamChunk>? progress, CancellationToken token,
        Func<Task>? onRecovery = null)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Another literary role is active.");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        _active = active;
        Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        using var diagnostics = new LiteraryRequestDiagnostics("Literary" + role, Log);
        _diagnostics = diagnostics;
        var requested = false;
        try
        {
            var ct = active.Token;
            var messages = LiteraryModelPolicy.Messages(role, history, draft, project);
            diagnostics.Write("input", messages);
            await PrepareAsync(ct).ConfigureAwait(false);
            diagnostics.Write("process_ready", new { pid = _process!.Id, role, slot = LiteraryModelPolicy.Slot(role), arguments = _process.StartInfo.ArgumentList.ToArray() });
            diagnostics.Watch(_process);
            var draftTokens = await TokenCountAsync(draft, false, ct);
            using var applied = await PostJsonAsync("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true }, ct);
            var promptTokens = await TokenCountAsync(applied.RootElement.GetProperty("prompt").GetString()!, true, ct);
            diagnostics.Write("budget", new { role, draftTokens, promptTokens, replyTokens = LiteraryModelPolicy.ReplyTokens(role), context = LiteraryModelPolicy.ContextTokens(role) });
            LiteraryModelPolicy.ValidateBudget(role, promptTokens, draftTokens);
            return await LiteraryLoopRecovery.RunAsync(async recovery =>
            {
                ct.ThrowIfCancellationRequested();
                // AwaitIdle can retire a server that did not acknowledge cancellation.
                await PrepareAsync(ct).ConfigureAwait(false);
                var body = LiteraryModelPolicy.Request(role, messages, recovery);
                diagnostics.Write("attempt", new { attempt = recovery ? 2 : 1, recovery });
                diagnostics.Write("request", new { endpoint = Server, json = body });
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Server, "v1/chat/completions"))
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                requested = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                diagnostics.Write("http", new { status = (int)response.StatusCode, headers = response.Headers.ToString() });
                if (!response.IsSuccessStatusCode) diagnostics.Write("http_error_body", await response.Content.ReadAsStringAsync(ct));
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var result = await LiteraryLoopStream.ReadAsync(stream, new DiagnosticProgress(progress, diagnostics),
                    line => diagnostics.Write("sse", line), ct).ConfigureAwait(false);
                diagnostics.Write("attempt_complete", new { recovery });
                diagnostics.Write("result", result);
                return result;
            }, async evidence =>
            {
                Log("Loop detected; one clean retry: " + evidence);
                diagnostics.Write("loop_recovery", evidence);
                // The failed attempt's using scopes have disposed the HTTP response before this callback.
                await AwaitIdleAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (onRecovery is not null) await onRecovery().ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is LiteraryLoopException loop) { diagnostics.Write("loop_stopped", loop.Evidence); Log("Loop stopped: " + loop.Evidence); }
            diagnostics.Write("failure", new { exception = ex.ToString(), userCancelled = token.IsCancellationRequested });
            Log("request failed: " + ex.GetType().Name + ": " + ex.Message);
            throw;
        }
        finally
        {
            // Disposing the streaming response cancels generation; preserve the other slot.
            try { if (requested) await AwaitIdleAsync().ConfigureAwait(false); }
            finally
            {
                _diagnostics = null; _active = null;
                Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke();
            }
        }
    }
    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken token)
    {
        using var response = await _http.PostAsync(new Uri(Server, path), new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) _diagnostics?.Write("preflight_error", new { path, status = (int)response.StatusCode, body = await response.Content.ReadAsStringAsync(token) });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private async Task<int> TokenCountAsync(string content, bool addSpecial, CancellationToken token)
    {
        using var result = await PostJsonAsync("tokenize", new { content, add_special = addSpecial }, token);
        return result.RootElement.GetProperty("tokens").GetArrayLength();
    }
    private async Task AwaitIdleAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (_process is { HasExited: false })
            {
                using var response = await _http.GetAsync(new Uri(Server, "slots"), timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (json.RootElement.EnumerateArray().All(s => !s.GetProperty("is_processing").GetBoolean())) return;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
        { Log("Could not confirm idle; restarting on next request: " + ex.Message); StopProcess(); }
    }
    private async Task PrepareAsync(CancellationToken token)
    {
        if (_process is { HasExited: false }) return;
        StopProcess();
        var model = await LiteraryModelLocation.ResolveAsync(token).ConfigureAwait(false);
        if (!File.Exists(LlamaBackendPaths.ServerExecutablePath)) throw new FileNotFoundException("Installed llama.cpp backend is required.");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
        {
            WorkingDirectory = LlamaBackendPaths.DirectoryPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_ARG_", StringComparison.Ordinal)).ToArray()) info.Environment.Remove(key);
        foreach (var arg in Arguments(model, _port)) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info };
        _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { Log(line); _diagnostics?.Write("stdout", line); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { Log(line); _diagnostics?.Write("stderr", line); } };
        _diagnostics?.Write("launch", new { executable = info.FileName, arguments = info.ArgumentList.ToArray(), model, modelBytes = new FileInfo(model).Length,
            backend = FileVersionInfo.GetVersionInfo(info.FileName).FileVersion });
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Literary runtime did not start.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Log(RuntimeResourceDiagnostics.DescribeLaunch("LiteraryShared", process, "shared weights; unified KV=24576; Writer=8192 Advisor=16384; slots=2; sequential", model));
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Literary runtime exited: " + process.ExitCode);
                try
                {
                    using var response = await _http.GetAsync(new Uri(Server, "health"), startup.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, startup.Token).ConfigureAwait(false);
            }
            Log(RuntimeResourceDiagnostics.DescribeSnapshot("LiteraryShared", process, "loaded"));
        }
        catch { StopProcess(); throw; }
    }
    private void Log(string line)
    {
        try { lock (_logGate) File.AppendAllText(_logPath, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public void Stop() { _active?.Cancel(); StopProcess(); }
    private void StopProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose() { Stop(); _http.Dispose(); }
    private sealed class DiagnosticProgress(IProgress<ModelStreamChunk>? target, LiteraryRequestDiagnostics diagnostics) : IProgress<ModelStreamChunk>
    {
        public void Report(ModelStreamChunk value) { diagnostics.Write("visible_chunk", value); target?.Report(value); }
    }
}
