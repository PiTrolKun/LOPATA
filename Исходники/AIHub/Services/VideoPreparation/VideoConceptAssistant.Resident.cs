using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoConceptModelRequest(string ModelId, string ModelPath, string EngineSetId)
{
    internal string? SongPlannerModelsRoot { get; init; }
    internal int? SongPlannerContext { get; init; }
}
public sealed record VideoConceptRuntimeInfo(int Pid, string ModelId, string Backend, string Device, string Mode, int Layers, int Context);
public interface IVideoConceptResident
{
    event Action? RuntimeChanged;
    VideoConceptRuntimeInfo? RuntimeInfo { get; }
    Task WarmAsync(VideoConceptModelRequest request, CancellationToken token);
    Task RetireAsync();
}

/// <summary>One audited worker across wizard requests; each generation still sends an independent context.</summary>
public sealed partial class VideoConceptAssistant
{
    private readonly SemaphoreSlim _residentGate = new(1, 1);
    private readonly LlamaServerRuntimeService _runtime;
    private readonly bool _adaptiveContext;
    private DebugModelInfo? _model;
    private OutputModelUse? _baseIdentity;
    private string _residentKey = "", _residentId = "";
    private int _capacity;
    private LlamaPlacementMode? _songPlannerPlacement;
    public event Action? RuntimeChanged;
    public Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? GpuAdmission { get; set; }
    public VideoConceptAssistant(UserContextService context) : this(context, adaptiveContext: false) { }

    internal VideoConceptAssistant(UserContextService context, bool adaptiveContext)
    {
        _runtime = new(context);
        _adaptiveContext = adaptiveContext;
    }
    public VideoConceptRuntimeInfo? RuntimeInfo => _runtime.PoetrySession is { } s
        ? new(s.Pid, _residentId, s.Backend, s.Device, s.Mode, s.Layers, s.Context) : null;
    public async Task WarmAsync(VideoConceptModelRequest request, CancellationToken token)
    {
        await _residentGate.WaitAsync(token);
        try { await EnsureResidentAsync(request, token); }
        catch { await _runtime.RetirePoetryAsync(); throw; }
        finally { _residentGate.Release(); RuntimeChanged?.Invoke(); }
    }
    private async Task EnsureResidentAsync(VideoConceptModelRequest request, CancellationToken token)
    {
        var songGemma = _adaptiveContext && request.ModelId == ManagedModelCatalog.LetItOutArtifactId
            && request.SongPlannerModelsRoot is not null;
        if ((!VideoConceptModels.Ids.Contains(request.ModelId) && !songGemma) || request.EngineSetId != EngineSetSelection.ActiveId)
            throw new BackgroundOperationWaitingException("VideoPrep.AiEngineChanged");
        var audited = await AuditModelAsync(request, token);
        var file = new FileInfo(request.ModelPath);
        var key = string.Join("|", request.ModelId, file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, request.EngineSetId);
        _baseIdentity = audited.Identity;
        if (_residentKey == key && _runtime.PoetrySession is { } resident)
        {
            _capacity = resident.Context;
            return;
        }
        await _runtime.RetirePoetryAsync();
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        _model = audited.Model; _residentId = request.ModelId;
        // Keep one worker across the wizard. Earlier editing/translation requests explicitly
        // disable thinking; scene planning can use a bounded reasoning stream without reload.
        var reasoning = MusicPoetryReasoning.SupportsId(request.ModelId) ? new MusicPoetryReasoningOptions(true, 1024) : MusicPoetryReasoningOptions.Disabled;
        // The background song planner uses automatic placement. The interactive video wizard
        // keeps its separate admission dialog and its existing context policy.
        var admission = _adaptiveContext ? null : GpuAdmission ?? ((_, _) =>
            Task.FromException<LlamaGpuDecision>(new BackgroundOperationWaitingException("VideoPrep.GpuNeedsDecision")));
        _capacity = await _runtime.PreparePoetryAsync(_model, token,
            requestedContextTokens: _adaptiveContext ? request.SongPlannerContext : VideoConceptModels.Context,
            reasoningOptions: reasoning, startupTimeout: TimeSpan.FromMinutes(4),
            gpuAdmission: admission, adaptivePlacement: _adaptiveContext, requireFullPrompt: _adaptiveContext,
            requiredPlacement: _songPlannerPlacement);
        _residentKey = key;
        RuntimeChanged?.Invoke();
    }

    internal async Task<bool> ExpandSongContextAsync(int required, VideoConceptModelRequest request, CancellationToken token)
    {
        if (!_adaptiveContext || request.SongPlannerContext.HasValue) return false;
        await _residentGate.WaitAsync(token);
        try
        {
            if (required <= _capacity) return true;
            var metadata = await Task.Run(() => LiteraryModelMemoryMetadata.Read(request.ModelPath), token);
            if (required > Math.Min(metadata.ModelContextTokens, LlamaDenseMemoryPolicy.MaximumCpuContext(metadata))) return false;
            var current = _runtime.PoetrySession?.Mode;
            if (current == "Cpu" || _songPlannerPlacement == LlamaPlacementMode.Cpu) return false;
            var remaining = current == "Hybrid" || _songPlannerPlacement == LlamaPlacementMode.Hybrid
                ? new[] { LlamaPlacementMode.Cpu } : new[] { LlamaPlacementMode.Hybrid, LlamaPlacementMode.Cpu };
            foreach (var placement in remaining)
            {
                _songPlannerPlacement = placement;
                _residentKey = "";
                try { await EnsureResidentAsync(request, token); }
                catch (IOException failure) when (failure.Message is "GameSounds.NotEnoughVram" or "Selected GPU VRAM monitoring is unavailable."
                    || failure.Message.StartsWith("Insufficient physical RAM for model and context:", StringComparison.Ordinal)
                    || NativeHardwareFailure.IsRecoverable(failure.Message))
                {
                    OwnedProcessRegistry.Log("song_planner_larger_context_unavailable", "SongClip", detail: failure.Message);
                    continue;
                }
                if (_capacity >= required) return true;
            }
            return false;
        }
        finally { _residentGate.Release(); }
    }
    public async Task RetireAsync()
    {
        await _residentGate.WaitAsync();
        try { await _runtime.RetirePoetryAsync(); _residentKey = ""; }
        finally { _residentGate.Release(); RuntimeChanged?.Invoke(); }
    }
}

public static class VideoConceptRuntimeLifecycle
{
    public static Task BeforeAsync(IVideoConceptAssistant assistant) => assistant is IVideoConceptResident
        ? Task.CompletedTask : ApplicationBackgroundOperations.RetireModelsAsync();
    public static Task AfterAsync(IVideoConceptAssistant assistant) => assistant is IVideoConceptResident
        ? Task.CompletedTask : ApplicationBackgroundOperations.RetireModelsAsync();
}
