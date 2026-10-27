using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public sealed record VideoConceptModelRequest(string ModelId, string ModelPath, string EngineSetId);
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
    private DebugModelInfo? _model;
    private OutputModelUse? _baseIdentity;
    private string _residentKey = "", _residentId = "";
    private int _capacity;
    public event Action? RuntimeChanged;
    public Func<LlamaGpuAdmission, CancellationToken, Task<LlamaGpuDecision>>? GpuAdmission { get; set; }
    public VideoConceptAssistant(UserContextService context) { _runtime = new(context); }
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
        if (!VideoConceptModels.Ids.Contains(request.ModelId) || request.EngineSetId != EngineSetSelection.ActiveId)
            throw new BackgroundOperationWaitingException("VideoPrep.AiEngineChanged");
        var audited = await AuditModelAsync(request, token);
        var file = new FileInfo(request.ModelPath);
        var key = string.Join("|", request.ModelId, file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, request.EngineSetId);
        _baseIdentity = audited.Identity;
        if (_residentKey == key && _runtime.PoetrySession is not null) return;
        await _runtime.RetirePoetryAsync();
        await ApplicationBackgroundOperations.RetireModelsExceptAsync(_runtime);
        _model = audited.Model; _residentId = request.ModelId;
        // Keep one worker across the wizard. Earlier editing/translation requests explicitly
        // disable thinking; scene planning can use a bounded reasoning stream without reload.
        var reasoning = MusicPoetryReasoning.SupportsId(request.ModelId) ? new MusicPoetryReasoningOptions(true, 1024) : MusicPoetryReasoningOptions.Disabled;
        _capacity = await _runtime.PreparePoetryAsync(_model, token, reasoning.Enabled ? "on" : "off", null, VideoConceptModels.Context,
            reasoning, TimeSpan.FromMinutes(4), GpuAdmission ?? ((_, _) =>
                Task.FromException<LlamaGpuDecision>(new BackgroundOperationWaitingException("VideoPrep.GpuNeedsDecision"))));
        _residentKey = key;
        RuntimeChanged?.Invoke();
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
