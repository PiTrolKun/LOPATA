using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Private text requests: no profile injection, session logger or backend output forwarding.</summary>
public sealed class FinancialModelRuntime : IDisposable
{
    private readonly LlamaServerRuntimeService _llama;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? _process;
    private string? _path;
    private int _port;
    public FinancialModelRuntime(UserContextService context)
    {
        _llama = new(context);
        ApplicationBackgroundOperations.RegisterModel(this, runtime => ModelProcessRetirement.StopAsync(runtime._process, runtime.Stop));
    }
    public Task<string> GenerateAsync(DebugModelInfo model, string system, string prompt, int maxTokens, CancellationToken token) =>
        GenerateConversationAsync(model, system, [new("user", prompt)], maxTokens, token, exploratory: false);

    public async Task<string> GenerateConversationAsync(DebugModelInfo model, string system, IReadOnlyList<FinancialDiscussionMessage> messages, int maxTokens, CancellationToken token, bool exploratory = true)
    {
        if (!File.Exists(model.Path)) throw new BackgroundOperationWaitingException("Finance.ModelMissing");
        // Thinking BIN models spend the same generation budget on reasoning and the final answer.
        var generationBudget = model.Format == "chatllm" ? Math.Max(4096, maxTokens) : maxTokens;
        var requestJson = RequestJson(model.Format, system, messages, generationBudget, exploratory);
        string endpoint;
        if (model.Format.Equals("gguf", StringComparison.OrdinalIgnoreCase))
        {
            await _llama.PrepareAsync(model, _ => { }, token); endpoint = _llama.Endpoint;
            using var requestData = JsonDocument.Parse(requestJson);
            using var templateBody = new StringContent(JsonSerializer.Serialize(new
            {
                messages = requestData.RootElement.GetProperty("messages"), add_generation_prompt = true,
                chat_template_kwargs = requestData.RootElement.GetProperty("chat_template_kwargs")
            }), Encoding.UTF8, "application/json");
            using var templateResponse = await _http.PostAsync(endpoint + "/apply-template", templateBody, token); templateResponse.EnsureSuccessStatusCode();
            using var applied = JsonDocument.Parse(await templateResponse.Content.ReadAsStringAsync(token));
            using var countBody = new StringContent(JsonSerializer.Serialize(new { content = applied.RootElement.GetProperty("prompt").GetString(), add_special = true }), Encoding.UTF8, "application/json");
            using var countResponse = await _http.PostAsync(endpoint + "/tokenize", countBody, token); countResponse.EnsureSuccessStatusCode();
            using var counts = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync(token));
            if (counts.RootElement.GetProperty("tokens").GetArrayLength() + maxTokens + 128 > CoreContextRuntimeLimits.CurrentBackendContextLimit)
                throw new BackgroundOperationWaitingException("Finance.ContextTooSmall");
        }
        else
        {
            if (!model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase)) throw new BackgroundOperationWaitingException("Finance.ModelUnsupported");
            await PrepareBinAsync(model.Path, token); endpoint = $"http://{IPAddress.Loopback}:{_port}";
        }
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(endpoint + "/v1/chat/completions", content, token);
        response.EnsureSuccessStatusCode();
        return ParseResponse(await response.Content.ReadAsStringAsync(token), generationBudget);
    }
    internal static string RequestJson(string format, string system, string prompt, int generationBudget) =>
        RequestJson(format, system, [new("user", prompt)], generationBudget, exploratory: false);

    internal static string RequestJson(string format, string system, IReadOnlyList<FinancialDiscussionMessage> messages, int generationBudget, bool exploratory = true)
    {
        if (messages.Count == 0 || messages.Any(m => m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Text)))
            throw new InvalidDataException("Invalid private conversation.");
        var body = new Dictionary<string, object>
        {
            ["model"] = "local", ["messages"] = new[] { new { role = "system", content = system } }
                .Concat(messages.Select(m => new { role = m.Role, content = m.Text })).ToArray(),
            ["max_tokens"] = generationBudget, ["temperature"] = exploratory ? 0.6 : 0.15, ["stream"] = false, ["cache_prompt"] = false
        };
        if (format.Equals("gguf", StringComparison.OrdinalIgnoreCase))
        {
            var thinking = system.Contains("/think", StringComparison.Ordinal);
            body["chat_template_kwargs"] = new { enable_thinking = thinking, reasoning_effort = "medium" };
            if (exploratory) { body["top_p"] = 0.95; body["top_k"] = 20; body["min_p"] = 0.0; }
        }
        return JsonSerializer.Serialize(body);
    }

    public static string ParseResponse(string body, int? generationBudget = null)
    {
        using var json = JsonDocument.Parse(body);
        var choice = json.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
            throw new BackgroundOperationWaitingException("Finance.ResponseTruncated");
        // chatllm v24 always reports 'stop', including token exhaustion (server.nim).
        if (generationBudget.HasValue && json.RootElement.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("completion_tokens", out var count) && count.TryGetInt32(out var generated) && generated >= generationBudget.Value)
            throw new BackgroundOperationWaitingException("Finance.ResponseTruncated");
        try { return ImageAnalysisKimiRequestBuilder.ParseResponseContent(body); }
        catch (InvalidDataException) { throw new BackgroundOperationWaitingException("Finance.InvalidModelReply"); }
    }
    private async Task PrepareBinAsync(string path, CancellationToken token)
    {
        if (_path == path && _process is { HasExited: false }) return;
        Stop();
        if (!File.Exists(ChatLlmBackendPaths.ServerExecutablePath)) throw new BackgroundOperationWaitingException("Finance.BackendMissing");
        await ComponentLicenseGate.EnsureAsync("basic", token);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(ChatLlmBackendPaths.ServerExecutablePath) { WorkingDirectory = ChatLlmBackendPaths.DirectoryPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = ImageAnalysisKimiRequestBuilder.BuildArguments(path, _port).ToArray();
        arguments[Array.IndexOf(arguments, "-c") + 1] = "8192";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = ChatLlmBackendPaths.ImageMagickDirectoryPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["MAGICK_HOME"] = ChatLlmBackendPaths.ImageMagickDirectoryPath;
        _process = new Process { StartInfo = start }; OwnedProcessRegistry.Shared.Start(_process, nameof(FinancialModelRuntime));
        _process.OutputDataReceived += (_, _) => { }; _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(8);
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested(); if (_process.HasExited) throw new InvalidOperationException("Finance backend exited.");
                try { using var ready = await _http.GetAsync($"http://{IPAddress.Loopback}:{_port}/health", token); if (ready.IsSuccessStatusCode) { _path = path; return; } }
                catch (HttpRequestException) { }
                await Task.Delay(1000, token);
            }
            throw new TimeoutException();
        }
        catch { Stop(); throw; }
    }
    public void Stop() { _llama.Stop(); if (_process is not null) { if (!_process.HasExited) _process.Kill(true); _process.Dispose(); _process = null; } _path = null; }
    public void Dispose() { Stop(); _llama.Dispose(); _http.Dispose(); }
}
