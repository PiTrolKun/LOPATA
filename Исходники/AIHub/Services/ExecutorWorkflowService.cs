using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ExecutorWorkflowService : IDisposable
{
    private readonly ExecutorModelArtifactResolver _resolver = new(new HuggingFaceExecutorArtifactSource());
    private readonly ExecutorModelInstaller _installer = new();
    private readonly UserContextService _userContextService;
    private readonly ModelSemanticPassportService _semanticPassportService;
    private ExecutorSessionService? _session;
    private ISessionEventLog? _sessionLog;
    private ExecutorModelArtifact? _pendingPassportArtifact;
    private int _autonomySeconds = CoreAutonomySettings.DefaultSeconds;
    private bool _disposed;

    public ExecutorWorkflowService(UserContextService userContextService)
    {
        _userContextService = userContextService;
        _semanticPassportService = new ModelSemanticPassportService(userContextService);
    }

    public event EventHandler<SessionKnowledgeTreeChangedEventArgs>? KnowledgeTreeChanged;

    public event EventHandler? CheckpointChanged;

    public bool BriefConfirmed => _session?.BriefConfirmed ?? false;

    public IReadOnlyList<ExecutorResultSnapshot> Snapshots =>
        _session?.Snapshots ?? [];

    public SessionKnowledgeTreeSnapshot? KnowledgeTreeSnapshot =>
        _session?.KnowledgeTree.GetSnapshot();

    public string ActiveLogPath => _sessionLog?.FilePath ?? string.Empty;

    public bool HasActiveSession => _session is not null;

    public void ConfigureAutonomy(int seconds)
    {
        _autonomySeconds = Math.Clamp(
            seconds,
            CoreAutonomySettings.MinimumSeconds,
            CoreAutonomySettings.MaximumSeconds);
        _session?.ConfigureAutonomy(_autonomySeconds);
    }

    public ExecutorSessionCheckpoint? CreateCheckpoint() =>
        _session?.CreateCheckpoint();

    public async Task<ExecutorModelArtifact> ResolveAsync(
        string requestedModel,
        StorageSettings storageSettings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var artifact = await _resolver.ResolveAsync(
            requestedModel,
            storageSettings,
            cancellationToken);
        if (artifact.IsInstalled)
        {
            _pendingPassportArtifact = artifact;
        }

        return artifact;
    }

    public async Task<ExecutorModelArtifact> InstallAsync(
        ExecutorModelArtifact artifact,
        StorageSettings storageSettings,
        IProgress<ExecutorDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var downloaded = await _installer.InstallAsync(artifact, storageSettings, progress, cancellationToken);
        using var runtime = new LlamaServerRuntimeService(_userContextService);
        var model = new DebugModelInfo
        {
            Name = downloaded.RepoId,
            Path = downloaded.InstalledPath,
            SizeBytes = downloaded.SizeBytes,
            Role = "executor",
            Status = "runtime_validation",
            Format = "gguf",
            IsRunnable = false
        };
        try
        {
            await runtime.ProbeModelAsync(model, _ => { }, cancellationToken);
            progress.Report(new ExecutorDownloadProgress(
                downloaded.SizeBytes,
                downloaded.SizeBytes,
                0,
                "installed"));
            var installed = _installer.MarkRuntimeVerified(downloaded);
            _pendingPassportArtifact = installed;
            return installed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _installer.MarkRuntimeIncompatible(downloaded, ex);
            throw new InvalidOperationException(
                $"The downloaded model failed the {LlamaBackendPaths.DisplayName} launch check and was not registered as runnable: {ex.Message}",
                ex);
        }
    }

    private async Task<ExecutorTurnResult> ExecuteAsyncCore(
        ExecutorModelArtifact artifact,
        ExecutorHandoffPackage handoff,
        SessionFileManifest sessionFileManifest,
        StorageSettings storageSettings,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backgroundStorage = storageSettings;
        Stop("replaced");
        _sessionLog = ScenarioSessionLog.CreateUncertaintyExecutor(
            storageSettings,
            handoff.ParentCoreSessionId,
            handoff.ParentRunId);
        _session = new ExecutorSessionService(_userContextService, _autonomySeconds);
        _session.BackgroundCheckpoint = SaveExecutorBackgroundCheckpoint;
        _session.BackgroundGeneration = BackgroundGeneration;
        _session.BackgroundTool = BackgroundTool;
        _session.KnowledgeTree.Changed += SessionKnowledgeTree_Changed;
        var turn = await _session.ExecuteAsync(
            artifact,
            handoff,
            sessionFileManifest,
            storageSettings,
            _sessionLog,
            streamProgress,
            cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    public ExecutorTurnResult Restore(
        ExecutorSessionCheckpoint checkpoint,
        ExecutorModelArtifact installedArtifact,
        SessionFileManifest sessionFileManifest,
        StorageSettings storageSettings,
        SessionRestorationContext restoration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _backgroundStorage = storageSettings;
        Stop("replaced_by_restored_session");
        _sessionLog = ScenarioSessionLog.CreateUncertaintyExecutor(
            storageSettings,
            restoration.SessionId,
            restoration.RunId);
        _session = new ExecutorSessionService(_userContextService, _autonomySeconds);
        _session.BackgroundCheckpoint = SaveExecutorBackgroundCheckpoint;
        _session.BackgroundGeneration = BackgroundGeneration;
        _session.BackgroundTool = BackgroundTool;
        _session.KnowledgeTree.Changed += SessionKnowledgeTree_Changed;
        var turn = _session.Restore(
            checkpoint,
            installedArtifact,
            sessionFileManifest,
            storageSettings,
            _sessionLog,
            restoration);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ContinueAsyncCore(
        string userResponse,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueAsync(userResponse, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ContinueAndRunAsyncCore(
        string userResponse,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueAsync(userResponse, streamProgress, cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ContinueApprovedActionAndRunAsyncCore(
        ExecutorTurnOption approvedOption,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueApprovedActionAsync(
            approvedOption,
            streamProgress,
            cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> UpdateFileManifestAsyncCore(
        SessionFileManifest fileManifest,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.UpdateFileManifestAsync(
            fileManifest,
            streamProgress,
            cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ConfirmBriefAndRunAsyncCore(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ConfirmBriefAsync(streamProgress, cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorResultSnapshot> CreateResultSnapshotAsyncCore(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var snapshot = await session.CreateResultSnapshotAsync(streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    private async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsyncCore(
        string capability,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueAfterCapabilityRequestAsync(
            capability,
            resultCode,
            details,
            streamProgress,
            cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsyncCore(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueAfterCapabilityRequestAsync(
            capabilities,
            resultCode,
            details,
            streamProgress,
            cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsyncCore(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        IReadOnlyCollection<CapabilityAdapterBinding> bindings,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var turn = await session.ContinueAfterCapabilityRequestAsync(
            capabilities,
            bindings,
            resultCode,
            details,
            streamProgress,
            cancellationToken);
        turn = await ResolveRequestedToolsAsync(session, turn, streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return turn;
    }

    private async Task<ExecutorResultSnapshot> CreateFinalResultAsyncCore(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session ?? throw new InvalidOperationException("Executor session is not active.");
        var snapshot = await session.CreateFinalResultAsync(streamProgress, cancellationToken);
        CheckpointChanged?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    public void Write(string eventType, object? payload = null) => _sessionLog?.Write(eventType, payload);

    private async Task<ExecutorTurnResult> ResolveRequestedToolsAsync(
        ExecutorSessionService session,
        ExecutorTurnResult turn,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        if (turn.Action != ExecutorTurnActions.RequestTool)
        {
            return turn;
        }

        var budget = new AutonomyExecutionBudget(_autonomySeconds);
        budget.Start();
        var request = 0;
        var lastFingerprint = string.Empty;
        while (budget.CanStartNext(request == 0))
        {
            if (turn.Action != ExecutorTurnActions.RequestTool)
            {
                return turn;
            }

            request++;
            var fingerprint = string.Join(
                "|",
                turn.RequestedTools.Order(StringComparer.Ordinal));
            if (!budget.RegisterProgress(fingerprint))
            {
                return session.CreateToolSafetyPause("tool_request_stagnation");
            }
            lastFingerprint = fingerprint;
            _sessionLog?.Write("executor_tool_request", new
            {
                Request = request,
                Stage = turn.StageId,
                Tools = turn.RequestedTools
            });
            try
            {
                turn = await session.EnableRequestedToolsAndContinueAsync(
                    turn.RequestedTools,
                    streamProgress,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _sessionLog?.Write("executor_tool_request_failed", new
                {
                    Request = request,
                    Stage = session.CurrentStageId,
                    ex.Message,
                    ErrorType = ex.GetType().FullName
                });
                return session.CreateToolSafetyPause(
                    $"tool_request_failed:{ex.GetType().Name}");
            }
        }

        _sessionLog?.Write("executor_tool_time_budget_reached", new
        {
            ElapsedMilliseconds = budget.Elapsed.TotalMilliseconds,
            LimitMilliseconds = budget.Limit.TotalMilliseconds,
            LastFingerprint = lastFingerprint
        });
        return session.CreateToolSafetyPause("tool_request_time_budget");
    }

    public void Stop(string reason)
    {
        _sessionLog?.Write("executor_session_end", new
        {
            Reason = reason,
            Stage = _session?.CurrentStageId,
            LastStatus = _session?.LastTurnStatus
        });
        _sessionLog?.Dispose();
        _sessionLog = null;
        if (_session is not null)
        {
            _session.KnowledgeTree.Changed -= SessionKnowledgeTree_Changed;
        }

        _session?.Dispose();
        _session = null;
    }

    public bool QueuePendingSemanticPassportGeneration(StorageSettings storageSettings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var artifact = _pendingPassportArtifact;
        _pendingPassportArtifact = null;
        return artifact is not null
            && _semanticPassportService.QueueGeneration(artifact, storageSettings);
    }

    private void SessionKnowledgeTree_Changed(
        object? sender,
        SessionKnowledgeTreeChangedEventArgs e) =>
        KnowledgeTreeChanged?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop("disposed");
        _installer.Dispose();
        _disposed = true;
    }
}
