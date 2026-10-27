using System.Text;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    private int? _poetryContextTokens;
    private int? _poetryRequestedContextTokens;
    private LlamaStartupJournal? _poetryJournal;
    private LlamaStartupPlacement? _poetryPlacement;
    private bool _poetryCanResize;
    private TimeSpan? _poetryStartupTimeout;
    private MusicPoetryReasoningOptions _poetryReasoning = MusicPoetryReasoningOptions.Disabled;
    private Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? _poetryGpuAdmission;
    private int RequestedContextTokens => _poetryContextTokens ?? CoreContextRuntimeLimits.CurrentBackendContextLimit;
    internal async Task<int> PreparePoetryAsync(DebugModelInfo model, CancellationToken token, string? reasoningMode = null,
        MusicPoetryReasoningPreference? reasoningPreference = null, int? requestedContextTokens = null,
        MusicPoetryReasoningOptions? reasoningOptions = null, TimeSpan? startupTimeout = null,
        Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? gpuAdmission = null)
    {
        var memory = await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), token);
        var capacity = Math.Min(CoreContextRuntimeLimits.CurrentBackendContextLimit, memory.ModelContextTokens);
        if (requestedContextTokens is { } requested) capacity = Math.Min(capacity, Math.Max(1024, requested));
        await RetirePoetryAsync();
        _poetryReasoning = reasoningOptions ?? MusicPoetryReasoning.Resolve(model, reasoningMode, reasoningPreference);
        _poetryGpuAdmission = gpuAdmission;
        _poetryStartupTimeout = startupTimeout;
        _poetryRequestedContextTokens = capacity;
        _poetryContextTokens = capacity;
        _poetryJournal = new(); _poetryCanResize = true;
        _poetryJournal.Record("begin", new { model = model.Name, model.Path, capacity,
            reasoningEnabled = _poetryReasoning.Enabled, reasoningBudget = _poetryReasoning.Budget,
            reasoningEffort = _poetryReasoning.Effort, preserveThoughts = _poetryReasoning.PreserveThoughts });
        try { await PrepareAsync(model, text => _poetryJournal.Record("runtime", text), token); }
        finally { _poetryCanResize = false; }
        return RequestedContextTokens;
    }
    internal sealed record SessionInfo(int Pid, string Backend, string Device, string Mode, int Layers, int Context);
    internal SessionInfo? PoetrySession
    {
        get
        {
            try { return _process is { HasExited: false } p && _poetryPlacement is { } place && _selectedRuntime is { } selected
                ? new(p.Id, selected.Bundle.Backend, selected.Device?.Name ?? "CPU", place.Mode.ToString(), place.GpuLayers, RequestedContextTokens) : null; }
            catch (InvalidOperationException) { return null; }
        }
    }
    internal async Task RetirePoetryAsync()
    {
        await ModelProcessRetirement.StopAsync(_process, Stop);
        await Task.WhenAll(_stderrPumpTask, _stdoutPumpTask);
        _poetryJournal?.Record("retired", null);
        _poetryReasoning = MusicPoetryReasoningOptions.Disabled;
        _poetryStartupTimeout = null;
    }
    private async Task RetirePoetryForRecoveryAsync()
    {
        var reasoning = _poetryReasoning;
        var startupTimeout = _poetryStartupTimeout;
        await RetirePoetryAsync();
        _poetryReasoning = reasoning;
        _poetryStartupTimeout = startupTimeout;
    }
    internal async Task<int> CountPoetryAsync(string system, IReadOnlyList<MusicPoetryMessage> messages, CancellationToken token)
    {
        using var template = new StringContent(JsonSerializer.Serialize(new {
            messages = MusicPoetryReasoningHistory.Messages(system, messages),
            add_generation_prompt = true, chat_template_kwargs = _poetryReasoning.TemplateOptions()
        }), Encoding.UTF8, "application/json");
        using var applied = await _httpClient.PostAsync(Endpoint + "/apply-template", template, token);
        applied.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await applied.Content.ReadAsStringAsync(token));
        using var input = new StringContent(JsonSerializer.Serialize(new {
            content = document.RootElement.GetProperty("prompt").GetString(), add_special = true
        }), Encoding.UTF8, "application/json");
        using var counted = await _httpClient.PostAsync(Endpoint + "/tokenize", input, token);
        counted.EnsureSuccessStatusCode();
        using var counts = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
        return counts.RootElement.GetProperty("tokens").GetArrayLength();
    }
}
