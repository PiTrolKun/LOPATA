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

public sealed partial class LlamaServerRuntimeService : IDisposable
{
    private readonly HttpClient _httpClient;
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
    private readonly bool _forceCpu;
    private volatile bool _hardwareStartupFailure;
    private LlamaRuntimeSelection? _selectedRuntime;

    public string ExpectedExecutablePath => _selectedRuntime?.Bundle.Server
        ?? LlamaRuntimeSelector.InstalledBundles().FirstOrDefault()?.Server
        ?? Path.Combine(new ComponentManager().GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!), "llama-server.exe");

    public bool IsAvailable => File.Exists(ExpectedExecutablePath);

    public string Endpoint => _port == 0 ? string.Empty : $"http://127.0.0.1:{_port}";

    public LlamaServerRuntimeService(UserContextService userContextService) : this(userContextService, false) { }

    internal LlamaServerRuntimeService(UserContextService userContextService, bool forceCpu, HttpMessageHandler? handler = null,
        LlamaPlacementPreferences? placementPreferences = null)
    {
        _placementPreferences = placementPreferences ?? LlamaPlacementPreferences.Default;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _forceCpu = forceCpu;
        _coreIdentityService = new CoreIdentityService(userContextService);
        ApplicationBackgroundOperations.RegisterModel(this, runtime => ModelProcessRetirement.StopAsync(runtime._process, runtime.Stop));
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

        var request = new ChatCompletionRequest
        {
            Messages = BuildMessages(model, history, userMessage),
            Temperature = 0.2,
            Stream = false
        };

        var result = await CompleteAsync(model, request, log, null, cancellationToken);
        return string.IsNullOrWhiteSpace(result.Content) ? "(empty response)" : result.Content;
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

        return (await CompleteAsync(model, request, log, null, cancellationToken)).Content;
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

        var request = new ChatCompletionRequest
        {
            Messages = BuildStructuredMessages(model, messages, interactionMode),
            Tools = tools.ToList(),
            ToolChoice = CreateToolChoice(requiredToolName),
            Temperature = 0.2,
            Stream = streamProgress is not null
        };

        return await CompleteAsync(model, request, log, streamProgress, cancellationToken);
    }

    public async Task<string> GenerateExecutorAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userPrompt,
        Action<string> log,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
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
        return (await CompleteAsync(model, request, log, streamProgress, cancellationToken)).Content;
    }

    public async Task<string> GenerateUtilityAsync(
        DebugModelInfo model,
        string systemPrompt,
        string userPrompt,
        Action<string> log,
        CancellationToken cancellationToken)
    {
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
        return (await CompleteAsync(model, request, log, null, cancellationToken)).Content;
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
        return (await CompleteAsync(model, request, log, streamProgress, cancellationToken)).Content;
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
        return await CompleteAsync(model, request, log, streamProgress, cancellationToken);
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

    private async Task EnsureStartedAsync(DebugModelInfo model, Action<string> log, CancellationToken cancellationToken, bool forceCpuFallback = false)
    {
        await _startupGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsAvailable)
            {
                throw new FileNotFoundException("llama-server.exe was not found.", ExpectedExecutablePath);
            }

            if (_process is not null
                && !_process.HasExited
                && (!forceCpuFallback || _selectedRuntime?.UsesGpu == false)
                && string.Equals(_currentModelPath, model.Path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ModelProcessRetirement.StopAsync(_process, Stop);
            var memory = await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), cancellationToken);
            if (_poetryJournal is not null)
            {
                await StartPoetryPlacementAsync(model, memory, log, cancellationToken, forceCpuFallback);
                return;
            }
            var selection = await LlamaRuntimeSelector.SelectAsync(LlamaDenseMemoryPolicy.GpuRequired(memory,
                    RequestedContextTokens), _forceCpu || forceCpuFallback, log, cancellationToken);
            if (!selection.UsesGpu) LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(memory, RequestedContextTokens);
            StartProcess(model, selection, log);
            try
            {
                await WaitForHealthAsync(log, cancellationToken);
            }
            catch (Exception ex) when ((ex is InvalidOperationException or TimeoutException)
                && selection.UsesGpu && _hardwareStartupFailure && !cancellationToken.IsCancellationRequested)
            {
                log($"Full GPU model startup failed ({ex.Message}). Retrying with CPU/RAM fallback.");
                await ModelProcessRetirement.StopAsync(_process, Stop);
                var cpu = await LlamaRuntimeSelector.SelectAsync(0, true, log, cancellationToken);
                LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(memory, RequestedContextTokens);
                StartProcess(model, cpu, log);
                await WaitForHealthAsync(log, cancellationToken);
            }
        }
        catch
        {
            await ModelProcessRetirement.StopAsync(_process, Stop);
            throw;
        }
        finally
        {
            _startupGate.Release();
        }
    }

    private void StartProcess(DebugModelInfo model, LlamaRuntimeSelection selection, Action<string> log)
    {
        _selectedRuntime = selection;
        _hardwareStartupFailure = false;
        var gpuLayers = selection.GpuLayers;
        _port = FindFreeLoopbackPort();
        _currentModelPath = model.Path;

        var startInfo = new ProcessStartInfo
        {
            FileName = selection.Bundle.Server,
            WorkingDirectory = selection.Bundle.Directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        RuntimeDeviceProbe.ClearBackendOverrides(startInfo);

        AddArgument(startInfo, "-m");
        AddArgument(startInfo, model.Path);
        AddArgument(startInfo, "--host");
        AddArgument(startInfo, "127.0.0.1");
        AddArgument(startInfo, "--port");
        AddArgument(startInfo, _port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--ctx-size");
        AddArgument(startInfo, RequestedContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--n-gpu-layers");
        AddArgument(startInfo, gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--device");
        AddArgument(startInfo, selection.DeviceId);
        AddArgument(startInfo, "--parallel");
        AddArgument(startInfo, "1");
        AddArgument(startInfo, "--jinja");
        AddArgument(startInfo, "--reasoning");
        AddArgument(startInfo, _poetryReasoning.Enabled ? "on" : "off");
        AddArgument(startInfo, "--no-webui");
        if (_poetryJournal is not null)
        {
            AddArgument(startInfo, "--reasoning-format"); AddArgument(startInfo, "deepseek");
            AddArgument(startInfo, "--reasoning-budget");
            AddArgument(startInfo, _poetryReasoning.Budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Keep placement/context under our staged policy; native auto-fit can silently change both.
            AddArgument(startInfo, "--fit"); AddArgument(startInfo, "off");
            AddArgument(startInfo, "--ubatch-size"); AddArgument(startInfo, "512");
            AddArgument(startInfo, "--cache-type-k"); AddArgument(startInfo, "f16");
            AddArgument(startInfo, "--cache-type-v"); AddArgument(startInfo, "f16");
            AddArgument(startInfo, "--log-verbosity"); AddArgument(startInfo, "4");
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        log($"Starting llama-server: {Path.GetFileName(model.Path)} on {Endpoint}; gpu-layers={gpuLayers}");
        OwnedProcessRegistry.Shared.Start(_process, "LlamaServerRuntimeService");
        _poetryJournal?.Record("launch", new { pid = _process.Id, executable = startInfo.FileName,
            arguments = startInfo.ArgumentList.ToArray(), backend = selection.Bundle.Backend,
            selection.DeviceId, gpuLayers, context = RequestedContextTokens });
        log(RuntimeResourceDiagnostics.DescribeLaunch(
            "AI HUB core / llama-server",
            _process,
            gpuLayers > 0
                ? $"GPU preferred; gpuLayers={gpuLayers}; llama.cpp/driver may use shared system memory"
                : "CPU/RAM",
            model.Path));
        _stdoutPumpTask = PumpOutputAsync(_process.StandardOutput, log, line => _poetryJournal?.Capture("stdout", line));
        var started = _process;
        _stderrPumpTask = PumpOutputAsync(_process.StandardError, log, line =>
        {
            _poetryJournal?.Capture("stderr", line);
            if (ReferenceEquals(_process, started))
            {
                _poetryAllocation?.Observe(line);
                if (IsHardwareAllocationFailure(line)) _hardwareStartupFailure = true;
            }
        });
    }

    private async Task WaitForHealthAsync(Action<string> log, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? sampleMemory = null)
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
            if (sampleMemory is not null) await sampleMemory(cancellationToken);

            try
            {
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                using var response = await _httpClient.GetAsync($"{Endpoint}/health", healthTimeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (sampleMemory is not null) await sampleMemory(cancellationToken);
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

            await Task.Delay(sampleMemory is null ? 1000 : 250, cancellationToken);
        }

        throw new TimeoutException("llama-server health check timed out.");
    }

    internal static bool IsHardwareAllocationFailure(string line) =>
        line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
        || line.Contains("VK_ERROR_OUT_OF", StringComparison.OrdinalIgnoreCase)
        || line.Contains("unsupported operation", StringComparison.OrdinalIgnoreCase);

    private static async Task PumpOutputAsync(StreamReader reader, Action<string> log, Action<string>? observe = null)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                observe?.Invoke(line);
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
