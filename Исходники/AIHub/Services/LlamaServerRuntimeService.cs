using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed class LlamaServerRuntimeService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly CoreIdentityService _coreIdentityService;
    private readonly SemaphoreSlim _startupGate = new(1, 1);

    private Process? _process;
    private string? _currentModelPath;
    private int _port;
    private bool _disposed;

    public string ExpectedExecutablePath { get; } = LlamaBackendPaths.ServerExecutablePath;

    public bool IsAvailable => File.Exists(ExpectedExecutablePath);

    public string Endpoint => _port == 0 ? string.Empty : $"http://127.0.0.1:{_port}";

    public LlamaServerRuntimeService(UserContextService userContextService)
    {
        _coreIdentityService = new CoreIdentityService(userContextService);
    }

    public Task PrepareAsync(
        DebugModelInfo model,
        Action<string> log,
        CancellationToken cancellationToken) =>
        EnsureStartedAsync(model, log, cancellationToken);

    public async Task<string> GenerateAsync(
        DebugModelInfo model,
        IReadOnlyList<DebugChatMessage> history,
        string userMessage,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(model, log, cancellationToken);

        var request = new ChatCompletionRequest
        {
            Messages = BuildMessages(model, history, userMessage),
            Temperature = 0.2,
            Stream = false
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{Endpoint}/v1/chat/completions",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
            stream,
            _jsonOptions,
            cancellationToken);

        var text = completion?.Choices.FirstOrDefault()?.Message.Content?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "(empty response)" : text;
    }

    public async Task<string> GenerateScenarioJsonAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userMessage,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        return await GenerateJsonAsync(
            model,
            systemPrompt,
            userMessage,
            ChoiceScenarioJsonContract.CreateResponseFormat(),
            log,
            cancellationToken);
    }

    public async Task<string> GenerateJsonAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userMessage,
        JsonObject responseFormat,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(model, log, cancellationToken);

        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "system",
                    Content = string.Join(
                        Environment.NewLine,
                        _coreIdentityService.BuildSystemPrompt(
                            model,
                            CoreInteractionMode.ScenarioPlanner,
                            "llama.cpp llama-server"),
                        string.Empty,
                        systemPrompt)
                },
                new ChatMessage { Role = "user", Content = userMessage }
            ],
            ResponseFormat = responseFormat,
            Temperature = 0.1,
            Stream = false
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{Endpoint}/v1/chat/completions",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
            stream,
            _jsonOptions,
            cancellationToken);

        return completion?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public async Task<StructuredChatResult> GenerateWithToolsAsync(
        DebugModelInfo model,
        IReadOnlyList<StructuredChatMessage> messages,
        IReadOnlyList<StructuredToolDefinition> tools,
        Action<string> log,
        CancellationToken cancellationToken,
        CoreInteractionMode interactionMode = CoreInteractionMode.StructuredToolAgent,
        string? requiredToolName = null,
        IProgress<ModelStreamChunk>? streamProgress = null)
    {
        await EnsureStartedAsync(model, log, cancellationToken);

        var request = new ChatCompletionRequest
        {
            Messages = BuildStructuredMessages(model, messages, interactionMode),
            Tools = tools.ToList(),
            ToolChoice = CreateToolChoice(requiredToolName),
            Temperature = 0.2,
            Stream = streamProgress is not null
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{Endpoint}/v1/chat/completions",
            content,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (streamProgress is not null)
        {
            return await OpenAiSseStreamParser.ReadAsync(stream, streamProgress, cancellationToken);
        }

        var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(stream, _jsonOptions, cancellationToken);

        var choice = completion?.Choices.FirstOrDefault();
        var message = choice?.Message;
        return new StructuredChatResult
        {
            Content = message?.Content?.Trim() ?? string.Empty,
            FinishReason = choice?.FinishReason ?? string.Empty,
            ToolCalls = message?.ToolCalls ?? []
        };
    }

    public async Task<string> GenerateExecutorAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userPrompt,
        Action<string> log,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(model, log, cancellationToken);
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = 0.2,
            Stream = true
        };
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{Endpoint}/v1/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await OpenAiSseStreamParser.ReadAsync(stream, streamProgress, cancellationToken);
        return result.Content;
    }

    public async Task<string> GenerateUtilityAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userPrompt,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(model, log, cancellationToken);
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = 0.1,
            MaxTokens = 700,
            Stream = false
        };
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{Endpoint}/v1/chat/completions",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
            stream,
            _jsonOptions,
            cancellationToken);
        return completion?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public async Task<string> GenerateTextAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        Action<string> log,
        CancellationToken cancellationToken,
        IProgress<ModelStreamChunk>? streamProgress = null)
    {
        await EnsureStartedAsync(model, log, cancellationToken);
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = Math.Clamp(temperature, 0, 2),
            MaxTokens = Math.Clamp(maxTokens, 128, 4096),
            Stream = streamProgress is not null
        };
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"{Endpoint}/v1/chat/completions",
            content,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (streamProgress is not null)
        {
            var streamed = await OpenAiSseStreamParser.ReadAsync(
                stream,
                streamProgress,
                cancellationToken);
            return streamed.Content.Trim();
        }

        var completion = await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
            stream,
            _jsonOptions,
            cancellationToken);
        return completion?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public async Task<StructuredChatResult> GenerateExternalWithToolsAsync(
        DebugModelInfo model,
        string systemPrompt,
        IReadOnlyList<StructuredChatMessage> messages,
        IReadOnlyList<StructuredToolDefinition> tools,
        Action<string> log,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken,
        JsonObject? responseFormat = null,
        string? requiredToolName = null)
    {
        await EnsureStartedAsync(model, log, cancellationToken);
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                .. messages.Select(message => new ChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    Name = message.Name,
                    ToolCallId = message.ToolCallId,
                    ToolCalls = message.ToolCalls
                })
            ],
            Tools = tools.Count == 0 ? null : tools.ToList(),
            ToolChoice = tools.Count == 0 ? null : CreateToolChoice(requiredToolName),
            ResponseFormat = responseFormat,
            Temperature = 0.2,
            Stream = true
        };
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"{Endpoint}/v1/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await OpenAiSseStreamParser.ReadAsync(stream, streamProgress, cancellationToken);
    }

    public async Task ProbeModelAsync(
        DebugModelInfo model,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureStartedAsync(model, log, cancellationToken);
        }
        finally
        {
            Stop();
        }
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Debug runtime shutdown is best-effort.
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _currentModelPath = null;
            _port = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _httpClient.Dispose();
        _disposed = true;
    }

    private async Task EnsureStartedAsync(DebugModelInfo model, Action<string> log, CancellationToken cancellationToken)
    {
        await ComponentLicenseGate.EnsureAsync("basic", cancellationToken);
        await _startupGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsAvailable)
            {
                throw new FileNotFoundException("llama-server.exe was not found.", ExpectedExecutablePath);
            }

            if (_process is not null
                && !_process.HasExited
                && string.Equals(_currentModelPath, model.Path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Stop();
            StartProcess(model, gpuLayers: 99, log);
            try
            {
                await WaitForHealthAsync(log, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
                                       && !cancellationToken.IsCancellationRequested)
            {
                log($"Full GPU model startup failed ({ex.Message}). Retrying with CPU/RAM fallback.");
                Stop();
                StartProcess(model, gpuLayers: 0, log);
                await WaitForHealthAsync(log, cancellationToken);
            }
        }
        catch
        {
            Stop();
            throw;
        }
        finally
        {
            _startupGate.Release();
        }
    }

    private void StartProcess(DebugModelInfo model, int gpuLayers, Action<string> log)
    {
        _port = FindFreeLoopbackPort();
        _currentModelPath = model.Path;

        var startInfo = new ProcessStartInfo
        {
            FileName = ExpectedExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(ExpectedExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        AddArgument(startInfo, "-m");
        AddArgument(startInfo, model.Path);
        AddArgument(startInfo, "--host");
        AddArgument(startInfo, "127.0.0.1");
        AddArgument(startInfo, "--port");
        AddArgument(startInfo, _port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--ctx-size");
        AddArgument(startInfo, CoreContextRuntimeLimits.CurrentBackendContextLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--n-gpu-layers");
        AddArgument(startInfo, gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--jinja");
        AddArgument(startInfo, "--reasoning");
        AddArgument(startInfo, "off");
        AddArgument(startInfo, "--no-webui");

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        log($"Starting llama-server: {Path.GetFileName(model.Path)} on {Endpoint}; gpu-layers={gpuLayers}");
        OwnedProcessRegistry.Shared.Start(_process, "LlamaServerRuntimeService");
        log(RuntimeResourceDiagnostics.DescribeLaunch(
            "AI HUB core / llama-server",
            _process,
            gpuLayers > 0
                ? $"GPU preferred; gpuLayers={gpuLayers}; llama.cpp/driver may use shared system memory"
                : "CPU/RAM",
            model.Path));
        _ = PumpOutputAsync(_process.StandardOutput, log);
        _ = PumpOutputAsync(_process.StandardError, log);
    }

    private async Task WaitForHealthAsync(Action<string> log, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null)
            {
                throw new InvalidOperationException("llama-server process was not started.");
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException($"llama-server exited with code {_process.ExitCode}.");
            }

            try
            {
                using var response = await _httpClient.GetAsync($"{Endpoint}/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    log($"llama-server health OK: {Endpoint}");
                    log(RuntimeResourceDiagnostics.DescribeSnapshot(
                        "AI HUB core / llama-server",
                        _process,
                        "ready"));
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Server is still loading the model.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retry until the startup deadline.
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException("llama-server health check timed out.");
    }

    private static async Task PumpOutputAsync(StreamReader reader, Action<string> log)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (ShouldLogBackendLine(line))
                {
                    log(line.Trim());
                }
            }
        }
        catch
        {
            // Process output pumping must not break the UI.
        }
    }

    private static bool ShouldLogBackendLine(string line)
    {
        return line.Contains("server is listening", StringComparison.OrdinalIgnoreCase)
            || line.Contains("model loaded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cuda", StringComparison.OrdinalIgnoreCase)
            || line.Contains("gpu", StringComparison.OrdinalIgnoreCase)
            || line.Contains("vram", StringComparison.OrdinalIgnoreCase)
            || line.Contains("offload", StringComparison.OrdinalIgnoreCase)
            || line.Contains("memory", StringComparison.OrdinalIgnoreCase)
            || line.Contains("prompt eval time", StringComparison.OrdinalIgnoreCase)
            || line.Contains("eval time", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private List<ChatMessage> BuildMessages(DebugModelInfo model, IReadOnlyList<DebugChatMessage> history, string userMessage)
    {
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = _coreIdentityService.BuildSystemPrompt(
                    model,
                    CoreInteractionMode.PlainChat,
                    "llama.cpp llama-server")
            }
        };

        foreach (var item in SelectHistoryForPrompt(history, 8))
        {
            messages.Add(new ChatMessage
            {
                Role = GetChatApiRole(item.Role),
                Content = item.Text
            });
        }

        messages.Add(new ChatMessage { Role = "user", Content = userMessage });
        return messages;
    }

    private List<ChatMessage> BuildStructuredMessages(
        DebugModelInfo model,
        IReadOnlyList<StructuredChatMessage> messages,
        CoreInteractionMode interactionMode)
    {
        var result = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = _coreIdentityService.BuildSystemPrompt(
                    model,
                    interactionMode,
                    "llama.cpp llama-server")
            }
        };

        foreach (var message in messages)
        {
            result.Add(new ChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                Name = message.Name,
                ToolCallId = message.ToolCallId,
                ToolCalls = message.ToolCalls
            });
        }

        return result;
    }

    private static IEnumerable<DebugChatMessage> SelectHistoryForPrompt(IReadOnlyList<DebugChatMessage> history, int recentCount)
    {
        return history
            .Where(item => IsMemoryRole(item.Role))
            .TakeLast(1)
            .Concat(history.Where(item => !IsMemoryRole(item.Role)).TakeLast(recentCount));
    }

    private static string GetChatApiRole(string role)
    {
        if (IsMemoryRole(role))
        {
            return "system";
        }

        return role.Contains("model", StringComparison.OrdinalIgnoreCase)
            || role.Contains("модель", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "user";
    }

    private static bool IsMemoryRole(string role) =>
        role.Contains("memory", StringComparison.OrdinalIgnoreCase)
        || role.Contains("память", StringComparison.OrdinalIgnoreCase);

    private static int FindFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string value)
    {
        startInfo.ArgumentList.Add(value);
    }

    private static JsonNode CreateToolChoice(string? requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName))
        {
            return JsonValue.Create("auto")!;
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = requiredToolName.Trim()
            }
        };
    }

    private sealed class ChatCompletionRequest
    {
        public List<ChatMessage> Messages { get; set; } = [];

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<StructuredToolDefinition>? Tools { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? ToolChoice { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonObject? ResponseFormat { get; set; }

        public double Temperature { get; set; }

        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        public string Role { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<StructuredToolCall>? ToolCalls { get; set; }
    }

    private sealed class ChatCompletionResponse
    {
        public List<ChatChoice> Choices { get; set; } = [];
    }

    private sealed class ChatChoice
    {
        public ChatMessage Message { get; set; } = new();

        public string FinishReason { get; set; } = string.Empty;
    }
}
