using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ExecutorSessionService : IDisposable
{
    private readonly LlamaServerRuntimeService _runtime;
    private readonly ExecutorToolGateway _toolGateway = new();
    private readonly SessionFileManifestService _fileManifestService = new();
    private readonly ExecutionActionGraphService _actionGraphService = new();
    private readonly ExecutionEvidenceService _evidenceService = new();
    private readonly ExecutionEvidenceProgressGuard _evidenceProgressGuard = new();
    private readonly List<StructuredChatMessage> _messages = [];
    private readonly List<ExecutorResultSnapshot> _snapshots = [];
    private readonly SessionKnowledgeTree _knowledgeTree = new();
    private readonly HashSet<string> _successfulToolCalls = new(StringComparer.Ordinal);
    private readonly List<ExecutionEvidenceReceipt> _evidenceReceipts = [];
    private ExecutionActionGraph _actionGraph = new();
    private ExecutorModelArtifact? _artifact;
    private ExecutorHandoffPackage? _handoff;
    private DebugModelInfo? _model;
    private string _systemPrompt = string.Empty;
    private string _restorationPrompt = string.Empty;
    private string _languageCode = "ru";
    private List<StructuredToolDefinition> _tools = [];
    private SessionFileManifest _sessionFileManifest = new();
    private StorageSettings? _storageSettings;
    private ISessionEventLog? _sessionLog;
    private ExecutorTurnResult? _lastTurn;
    private string _currentStageId = ExecutorStageIds.TaskDefinition;
    private string _confirmedBriefCheckpoint = string.Empty;
    private bool _briefConfirmed;
    private int _snapshotVersion;
    private long _successfulToolSequence;
    private bool _disposed;
    private int _autonomySeconds;

    public ExecutorSessionService(
        UserContextService userContextService,
        int autonomySeconds = CoreAutonomySettings.DefaultSeconds)
    {
        _runtime = new LlamaServerRuntimeService(userContextService);
        ConfigureAutonomy(autonomySeconds);
        _knowledgeTree.Changed += KnowledgeTree_Changed;
    }

    public void ConfigureAutonomy(int seconds)
    {
        _autonomySeconds = Math.Clamp(
            seconds,
            CoreAutonomySettings.MinimumSeconds,
            CoreAutonomySettings.MaximumSeconds);
    }

    public string CurrentStageId => _currentStageId;

    public string LastTurnStatus => _lastTurn?.Status ?? "not_started";

    public bool BriefConfirmed => _briefConfirmed;

    public IReadOnlyList<ExecutorResultSnapshot> Snapshots => _snapshots;

    public SessionKnowledgeTree KnowledgeTree => _knowledgeTree;

    public ExecutorTurnResult? LastTurn => _lastTurn;

    public ExecutorSessionCheckpoint CreateCheckpoint()
    {
        EnsureActive();
        return new ExecutorSessionCheckpoint
        {
            Artifact = Clone(_artifact!),
            Handoff = Clone(_handoff!),
            Messages = Clone(_messages),
            LastTurn = _lastTurn is null ? null : Clone(_lastTurn),
            CurrentStageId = _currentStageId,
            ConfirmedBriefCheckpoint = _confirmedBriefCheckpoint,
            BriefConfirmed = _briefConfirmed,
            SnapshotVersion = _snapshotVersion,
            Snapshots = Clone(_snapshots),
            KnowledgeTree = _knowledgeTree.GetSnapshot(),
            EnabledTools = _tools
                .Select(tool => tool.Function.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            SuccessfulToolCalls = [.. _successfulToolCalls],
            ActionGraph = Clone(_actionGraph),
            EvidenceReceipts = Clone(_evidenceReceipts),
            BackgroundLoopPending = _backgroundLoopPending,
            PendingRequiredTool = _pendingRequiredTool,
            PendingRequiredTarget = _pendingRequiredTarget,
            PendingRequiredToolSatisfied = _pendingRequiredToolSatisfied,
            PendingToolCalls = Clone(_pendingToolCalls),
            InFlightToolCallId = _inFlightToolCallId,
            PendingSnapshotMarkdown = _pendingSnapshotMarkdown,
            PendingSnapshotId = _pendingSnapshotId
        };
    }

    public async Task<ExecutorTurnResult> ExecuteAsync(
        ExecutorModelArtifact artifact,
        ExecutorHandoffPackage handoff,
        SessionFileManifest sessionFileManifest,
        StorageSettings storageSettings,
        ISessionEventLog sessionLog,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!artifact.IsInstalled || !File.Exists(artifact.InstalledPath))
        {
            throw new FileNotFoundException("The executor model is not installed.", artifact.InstalledPath);
        }

        _artifact = Clone(artifact);
        _handoff = Clone(handoff);
        _sessionFileManifest = Clone(sessionFileManifest);
        _handoff.FileManifest = _fileManifestService.CreatePromptManifest(
            _sessionFileManifest,
            contentAccessAvailable: true);
        _model = new DebugModelInfo
        {
            Name = artifact.RepoId,
            Path = artifact.InstalledPath,
            SizeBytes = artifact.SizeBytes,
            Role = "executor",
            Status = "installed",
            Format = "gguf",
            IsRunnable = true
        };
        _actionGraph = _actionGraphService.Build(_handoff);
        _actionGraphService.BindInputFiles(_actionGraph, _sessionFileManifest);
        _evidenceReceipts.Clear();
        _evidenceProgressGuard.Reset(_actionGraph);
        _systemPrompt = string.Join(
            Environment.NewLine,
            BuildSystemPrompt(_handoff),
            string.Empty,
            BuildActionGraphPrompt(_actionGraph));
        _restorationPrompt = string.Empty;
        _languageCode = _handoff.LanguageCode;
        _storageSettings = storageSettings;
        _sessionLog = sessionLog;
        _knowledgeTree.Initialize(_handoff);
        _currentStageId = ExecutorStageIds.TaskDefinition;
        _confirmedBriefCheckpoint = string.Empty;
        _lastTurn = null;
        _briefConfirmed = false;
        _snapshotVersion = 0;
        _successfulToolSequence = 0;
        _successfulToolCalls.Clear();
        _snapshots.Clear();
        _messages.Clear();
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                BuildUserPrompt(_handoff, _currentStageId),
                BuildTreeContextMessage())
        });
        sessionLog.Write("executor_session_start", new
        {
            Model = artifact.RepoId,
            artifact.FileName,
            artifact.Quantization,
            InitialStage = _currentStageId,
            Handoff = _handoff,
            ActionGraph = _actionGraph
        });

        _tools = ExecutorToolCatalog.CreateDefinitions(
            includeWeb: _handoff.NeedsWeb,
            includeSessionFiles: HasAvailableSessionFiles(),
            adapterToolNames: GetReadyAdapterToolNames(_handoff));
        return await RunLoopAsync(streamProgress, cancellationToken);
    }

    public ExecutorTurnResult Restore(
        ExecutorSessionCheckpoint checkpoint,
        ExecutorModelArtifact installedArtifact,
        SessionFileManifest sessionFileManifest,
        StorageSettings storageSettings,
        ISessionEventLog sessionLog,
        SessionRestorationContext restoration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(restoration);
        if (!installedArtifact.IsInstalled || !File.Exists(installedArtifact.InstalledPath))
        {
            throw new FileNotFoundException(
                "The executor model required by the saved session is not installed.",
                installedArtifact.InstalledPath);
        }

        _artifact = Clone(installedArtifact);
        _handoff = Clone(checkpoint.Handoff);
        _sessionFileManifest = Clone(sessionFileManifest);
        _handoff.FileManifest = _fileManifestService.CreatePromptManifest(
            _sessionFileManifest,
            contentAccessAvailable: true);
        _model = new DebugModelInfo
        {
            Name = installedArtifact.RepoId,
            Path = installedArtifact.InstalledPath,
            SizeBytes = installedArtifact.SizeBytes,
            Role = "executor",
            Status = "installed",
            Format = "gguf",
            IsRunnable = true
        };
        _actionGraph = checkpoint.ActionGraph?.Nodes?.Count > 0
            ? Clone(checkpoint.ActionGraph)
            : _actionGraphService.Build(_handoff);
        _actionGraphService.BindInputFiles(_actionGraph, _sessionFileManifest);
        _evidenceReceipts.Clear();
        _evidenceReceipts.AddRange(Clone(checkpoint.EvidenceReceipts ?? []));
        _actionGraphService.Reconcile(_actionGraph, _evidenceReceipts);
        _evidenceProgressGuard.Reset(_actionGraph);
        _restorationPrompt = BuildRestorationPrompt(restoration);
        _systemPrompt = string.Join(
            Environment.NewLine,
            BuildSystemPrompt(_handoff),
            string.Empty,
            BuildActionGraphPrompt(_actionGraph),
            string.Empty,
            _restorationPrompt);
        _languageCode = _handoff.LanguageCode;
        _storageSettings = storageSettings;
        _sessionLog = sessionLog;
        _currentStageId = checkpoint.CurrentStageId;
        _confirmedBriefCheckpoint = checkpoint.ConfirmedBriefCheckpoint;
        _briefConfirmed = checkpoint.BriefConfirmed;
        _snapshotVersion = checkpoint.SnapshotVersion;
        _successfulToolSequence = 0;
        _successfulToolCalls.Clear();
        _successfulToolCalls.UnionWith(checkpoint.SuccessfulToolCalls ?? []);
        _lastTurn = checkpoint.LastTurn is null ? null : Clone(checkpoint.LastTurn);
        _backgroundLoopPending = checkpoint.BackgroundLoopPending;
        _pendingRequiredTool = checkpoint.PendingRequiredTool;
        _pendingRequiredTarget = checkpoint.PendingRequiredTarget;
        _pendingRequiredToolSatisfied = checkpoint.PendingRequiredToolSatisfied;
        _pendingToolCalls.Clear(); _pendingToolCalls.AddRange(Clone(checkpoint.PendingToolCalls));
        _inFlightToolCallId = checkpoint.InFlightToolCallId;
        _pendingSnapshotMarkdown = checkpoint.PendingSnapshotMarkdown;
        _pendingSnapshotId = checkpoint.PendingSnapshotId;
        _messages.Clear();
        _messages.AddRange(Clone(checkpoint.Messages));
        _snapshots.Clear();
        _snapshots.AddRange(Clone(checkpoint.Snapshots));
        _tools = ExecutorToolCatalog.CreateDefinitions(
                includeWeb: true,
                includeSessionFiles: HasAvailableSessionFiles(),
                adapterToolNames: checkpoint.EnabledTools.Concat(
                    GetReadyAdapterToolNames(_handoff)))
            .Where(tool => ExecutorToolCatalog.IsSessionFileTool(tool.Function.Name)
                || checkpoint.EnabledTools.Contains(
                    tool.Function.Name,
                    StringComparer.Ordinal))
            .ToList();
        _knowledgeTree.Restore(checkpoint.KnowledgeTree);
        sessionLog.Write("executor_session_restored", new
        {
            restoration.SessionId,
            restoration.RunId,
            restoration.ResumeCount,
            restoration.PreviousStopKind,
            restoration.PreviousStopReason,
            restoration.LostUncommittedTurn,
            Stage = _currentStageId,
            Model = installedArtifact.RepoId
        });

        return _lastTurn ?? (_backgroundLoopPending ? new ExecutorTurnResult() : null)
            ?? throw new InvalidOperationException("The saved executor session has no stable turn.");
    }

    public async Task<ExecutorTurnResult> ContinueAsync(
        string userResponse,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_model is null || _storageSettings is null || _sessionLog is null)
        {
            throw new InvalidOperationException("Executor session has not been started.");
        }

        _knowledgeTree.RecordAnswer(userResponse);
        var responseMessageIndex = _messages.Count;
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_USER_RESPONSE]",
                $"Current stage: {_currentStageId}",
                "User selected or entered this response:",
                userResponse.Trim(),
                BuildTreeContextMessage(),
                _briefConfirmed
                    ? "Use this answer to add a substantive workingResultFragment, improve currentResultSummary, then ask exactly one next useful practical question."
                    : "Use this answer only to improve the technical task definition.",
                "Do not change stages. Do not create a full result unless AI HUB sends a separate result snapshot request.")
        });
        _sessionLog.Write("executor_user_response", new
        {
            Stage = _currentStageId,
            Response = userResponse
        });
        try
        {
            return await RunLoopAsync(
                streamProgress,
                cancellationToken,
                GetRequiredFileEvidenceTool());
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            if (_messages.Count > responseMessageIndex)
            {
                _messages.RemoveRange(responseMessageIndex, _messages.Count - responseMessageIndex);
            }

            _sessionLog.Write("executor_user_response_failed", new
            {
                Stage = _currentStageId,
                Response = userResponse
            });
            throw;
        }
    }

    public async Task<ExecutorTurnResult> ContinueApprovedActionAsync(
        ExecutorTurnOption approvedOption,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(approvedOption);
        if (approvedOption.Intent != ExecutorOptionIntents.ApproveAction
            || !ExecutorToolCatalog.IsSessionFileTool(approvedOption.Action)
            || _lastTurn?.Options.Any(option =>
                option.Intent == ExecutorOptionIntents.ApproveAction
                && string.Equals(option.Action, approvedOption.Action, StringComparison.Ordinal)
                && string.Equals(option.TargetId, approvedOption.TargetId, StringComparison.Ordinal)) != true)
        {
            throw new InvalidOperationException("The selected executor action is not active or allowed.");
        }

        var messageIndex = _messages.Count;
        _knowledgeTree.RecordAnswer(approvedOption.Title);
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_USER_APPROVED_ACTION]",
                $"Current stage: {_currentStageId}",
                $"Approved action: {approvedOption.Action}",
                $"Target file id: {approvedOption.TargetId}",
                $"Expected effect: {approvedOption.Effect}",
                "Execute the approved safe tool now. Do not replace it with a promise or another confirmation question.",
                "After a successful tool result, use the evidence in the current task and continue with exactly one useful question.",
                BuildTreeContextMessage())
        });
        _sessionLog!.Write("executor_action_approved", new
        {
            Stage = _currentStageId,
            approvedOption.Title,
            approvedOption.Action,
            approvedOption.TargetId,
            approvedOption.Effect
        });
        try
        {
            var successfulToolSequenceBefore = _successfulToolSequence;
            var turn = await RunLoopAsync(
                streamProgress,
                cancellationToken,
                approvedOption.Action, approvedOption.TargetId);
            if (_successfulToolSequence <= successfulToolSequenceBefore
                || !_successfulToolCalls.Contains(
                CreateToolEvidenceKey(approvedOption.Action, approvedOption.TargetId)))
            {
                return CreateToolSafetyPause($"approved_action_failed:{approvedOption.Action}");
            }

            return turn;
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            if (_messages.Count > messageIndex)
            {
                _messages.RemoveRange(messageIndex, _messages.Count - messageIndex);
            }

            throw;
        }
    }

    public async Task<ExecutorTurnResult> UpdateFileManifestAsync(
        SessionFileManifest fileManifest,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(fileManifest);
        var previousSessionManifest = Clone(_sessionFileManifest);
        var previousPromptManifest = Clone(_handoff!.FileManifest);
        var messageIndex = _messages.Count;
        _sessionFileManifest = Clone(fileManifest);
        _handoff.FileManifest = _fileManifestService.CreatePromptManifest(
            _sessionFileManifest,
            contentAccessAvailable: true);
        RefreshSessionFileTools();
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_FILE_MANIFEST_UPDATED]",
                $"Current stage: {_currentStageId}",
                "AI HUB updated trusted metadata about files selected by the user:",
                JsonSerializer.Serialize(_handoff.FileManifest),
                "This is not an answer to your current question.",
                "Absolute paths are unavailable. Use session_files_list, session_file_inspect and session_file_read when actual supported file content is relevant.",
                "Do not claim that you read or analyzed a file unless a successful tool result contains the needed evidence.",
                "A newly added file may be primary task input, a separate example/reference, or explanatory context.",
                "If its role is not already clear from the conversation, ask specifically which role it has. Do not assume that every added file must be processed in the same way.",
                "For that role question, provide ready selectable options for: primary task input; separate example/reference; explanatory context; do not use. Never rely on custom text for these standard roles.",
                "Re-evaluate the current task definition and ask exactly one next useful question. Do not change stages.",
                BuildTreeContextMessage())
        });
        _sessionLog!.Write("executor_file_manifest_updated", _handoff.FileManifest);
        try
        {
            return await RunLoopAsync(
                streamProgress,
                cancellationToken,
                GetRequiredFileEvidenceTool());
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            _sessionFileManifest = previousSessionManifest;
            _handoff.FileManifest = previousPromptManifest;
            RefreshSessionFileTools();
            if (_messages.Count > messageIndex)
            {
                _messages.RemoveRange(messageIndex, _messages.Count - messageIndex);
            }

            _sessionLog.Write("executor_file_manifest_update_failed", new
            {
                Stage = _currentStageId
            });
            throw;
        }
    }

    public async Task<ExecutorTurnResult> ConfirmBriefAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        if (_briefConfirmed
            || _currentStageId != ExecutorStageIds.TaskDefinition
            || _lastTurn?.Status != ExecutorTurnStatuses.StageReady
            || _lastTurn.Action != ExecutorTurnActions.ConfirmBrief)
        {
            throw new InvalidOperationException("The executor brief is not ready for confirmation.");
        }

        var checkpoint = BuildStageCheckpoint(_lastTurn);
        _confirmedBriefCheckpoint = checkpoint;
        _briefConfirmed = true;
        _knowledgeTree.RecordBriefConfirmation(checkpoint);
        _sessionLog!.Write("executor_brief_confirmed", new
        {
            Stage = _currentStageId,
            Brief = checkpoint
        });
        try
        {
            return await TransitionStageInternalAsync(
                ExecutorStageIds.PracticalClarification,
                "brief_confirmation",
                checkpoint,
                streamProgress,
                cancellationToken);
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            _briefConfirmed = false;
            _confirmedBriefCheckpoint = string.Empty;
            _sessionLog.Write("executor_brief_confirmation_rolled_back", new
            {
                Stage = _currentStageId
            });
            throw;
        }
    }

    public async Task<ExecutorTurnResult> EnableRequestedToolsAndContinueAsync(
        IReadOnlyCollection<string> requestedTools,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        var allowedNames = requestedTools
            .Where(name => name is "web_search" or "web_research" or "web_read")
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var enabled = ExecutorToolCatalog.CreateDefinitions(
                includeWeb: true,
                includeSessionFiles: false)
            .Where(tool => allowedNames.Contains(tool.Function.Name))
            .ToList();
        foreach (var tool in enabled)
        {
            if (_tools.All(existing => existing.Function.Name != tool.Function.Name))
            {
                _tools.Add(tool);
            }
        }

        _sessionLog!.Write("executor_tools_enabled", new
        {
            Stage = _currentStageId,
            Requested = requestedTools,
            Enabled = enabled.Select(tool => tool.Function.Name).ToArray()
        });
        if (enabled.Count == 0)
        {
            return CreateSafetyPause(
                "The requested tool is unavailable.",
                "Укажите, как продолжить без недоступного инструмента.",
                "Explain how to continue without the unavailable tool.");
        }

        var messageIndex = _messages.Count;
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_TOOLS_ENABLED]",
                $"Current stage: {_currentStageId}",
                $"Enabled safe tools: {string.Join(", ", enabled.Select(tool => tool.Function.Name))}.",
                "Use a tool only when it is genuinely required.",
                BuildTreeContextMessage(),
                "After tool use, add a substantive workingResultFragment, update currentResultSummary and return exactly one practical question to the user.")
        });
        try
        {
            return await RunLoopAsync(streamProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            if (_messages.Count > messageIndex)
            {
                _messages.RemoveRange(messageIndex, _messages.Count - messageIndex);
            }

            throw;
        }
    }

    public async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        string capability,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        return await ContinueAfterCapabilityRequestAsync(
            [
                new ExecutorCapabilityRequest
                {
                    Id = capability,
                    Purpose = details,
                    Required = true
                }
            ],
            resultCode,
            details,
            streamProgress,
            cancellationToken);
    }

    public async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        return await ContinueAfterCapabilityRequestAsync(
            capabilities,
            [],
            resultCode,
            details,
            streamProgress,
            cancellationToken);
    }

    public async Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        IReadOnlyCollection<CapabilityAdapterBinding> bindings,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        var normalizedCapabilities = capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Id))
            .Select(capability => new ExecutorCapabilityRequest
            {
                Id = capability.Id.Trim().ToLowerInvariant(),
                Purpose = capability.Purpose.Trim(),
                Required = capability.Required,
                Alternatives = capability.Alternatives
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .GroupBy(capability => capability.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (normalizedCapabilities.Count == 0)
        {
            throw new InvalidOperationException("The capability result does not contain any capability IDs.");
        }

        _actionGraphService.MergeCapabilities(
            _actionGraph,
            normalizedCapabilities,
            bindings,
            _evidenceReceipts);
        _actionGraphService.BindInputFiles(_actionGraph, _sessionFileManifest);
        _actionGraphService.Reconcile(_actionGraph, _evidenceReceipts);

        var enabledAdapterTools = bindings
            .Where(binding => binding.IsExecutable)
            .SelectMany(binding => binding.ToolNames)
            .Where(ExecutorToolCatalog.IsAdapterTool)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var tool in ExecutorToolCatalog.CreateDefinitions(
                     includeWeb: false,
                     includeSessionFiles: false,
                     adapterToolNames: enabledAdapterTools))
        {
            if (_tools.All(existing =>
                    !string.Equals(
                        existing.Function.Name,
                        tool.Function.Name,
                        StringComparison.Ordinal)))
            {
                _tools.Add(tool);
            }
        }

        var messageIndex = _messages.Count;
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_CAPABILITY_RESULT]",
                $"Current stage: {_currentStageId}",
                "Capabilities:",
                string.Join(
                    Environment.NewLine,
                    normalizedCapabilities.Select(capability =>
                        $"- {capability.Id}; required={capability.Required}; purpose={capability.Purpose}")),
                $"Result: {resultCode}",
                $"Details: {details}",
                $"Enabled specialist tools: {string.Join(", ", enabledAdapterTools)}.",
                BuildActionGraphPrompt(_actionGraph),
                "This is a trusted program result, not a user answer.",
                "Continue the same task and context. A package is usable only when the result explicitly says that its trusted adapter and tool schema are ready.",
                "If external discovery is authorized, research alternatives but do not claim that an unverified package is callable.",
                "If denied, unavailable or missing an adapter, use a realistic fallback or explain the blocking limitation.",
                BuildTreeContextMessage())
        });
        _sessionLog!.Write("executor_capability_result", new
        {
            Stage = _currentStageId,
            Capabilities = normalizedCapabilities,
            Result = resultCode,
            Details = details,
            EnabledSpecialistTools = enabledAdapterTools,
            ActionGraph = _actionGraph
        });
        try
        {
            return await RunLoopAsync(streamProgress, cancellationToken);
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            if (_messages.Count > messageIndex)
            {
                _messages.RemoveRange(messageIndex, _messages.Count - messageIndex);
            }

            throw;
        }
    }

    private async Task<ExecutorTurnResult> TransitionStageInternalAsync(
        string targetStageId,
        string initiatedBy,
        string? checkpointOverride,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();

        if (_briefConfirmed is false
            || _currentStageId != ExecutorStageIds.TaskDefinition
            || targetStageId != ExecutorStageIds.PracticalClarification)
        {
            throw new InvalidOperationException("The requested executor stage transition is not allowed.");
        }

        var previousStageId = _currentStageId;
        var checkpoint = checkpointOverride ?? BuildStageCheckpoint(_lastTurn);
        var transitionMessageIndex = _messages.Count;
        var controlMessage = new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "[AI_HUB_STAGE_CHANGE]",
                $"The user confirmed the technical brief. AI HUB moved the session from {previousStageId} to {targetStageId}.",
                $"Previous stage checkpoint: {checkpoint}",
                BuildStageInstruction(targetStageId),
                BuildTreeContextMessage(),
                "Create the first substantive workingResultFragment, derive a compact currentResultSummary from actual answer content, and ask the first practical question.",
                "This is the final persistent stage. Never request another stage change.")
        };
        _messages.Add(controlMessage);
        _currentStageId = targetStageId;
        _sessionLog!.Write("executor_stage_transition", new
        {
            From = previousStageId,
            To = targetStageId,
            Checkpoint = checkpoint,
            InitiatedBy = initiatedBy
        });

        try
        {
            return await RunLoopAsync(
                streamProgress,
                cancellationToken,
                GetRequiredFileEvidenceTool());
        }
        catch (OperationCanceledException) when (ApplicationBackgroundOperations.IsSuspending(cancellationToken))
        {
            PersistBackgroundCheckpoint();
            throw;
        }
        catch
        {
            if (_messages.Count > transitionMessageIndex)
            {
                _messages.RemoveRange(transitionMessageIndex, _messages.Count - transitionMessageIndex);
            }

            _currentStageId = previousStageId;
            _sessionLog.Write("executor_stage_transition_failed", new
            {
                From = previousStageId,
                To = targetStageId
            });
            throw;
        }
    }

    public Task<ExecutorResultSnapshot> CreateResultSnapshotAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        if (ApplicationBackgroundOperations.Current?.IsInOperationScope != true) BeginBackgroundSnapshot();
        return CreateResultDocumentAsync(isFinal: false, streamProgress, cancellationToken);
    }

    public Task<ExecutorResultSnapshot> CreateFinalResultAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        if (ApplicationBackgroundOperations.Current?.IsInOperationScope != true) BeginBackgroundSnapshot();
        return CreateResultDocumentAsync(isFinal: true, streamProgress, cancellationToken);
    }

    private async Task<ExecutorResultSnapshot> CreateResultDocumentAsync(
        bool isFinal,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        var savedSnapshot = _snapshots.FirstOrDefault(s => s.Id == _pendingSnapshotId);
        if (savedSnapshot is not null) return savedSnapshot;
        if (!_briefConfirmed)
        {
            throw new InvalidOperationException("The executor brief has not been confirmed.");
        }
        if (GetRequiredFileEvidenceTool() is not null)
        {
            throw new InvalidOperationException(
                "The executor has not read the required session file through an AI HUB tool.");
        }

        _actionGraphService.Reconcile(_actionGraph, _evidenceReceipts);
        var evidenceValidation = _evidenceService.Validate(
            _actionGraph,
            _evidenceReceipts);

        var model = _model!;
        var sessionLog = _sessionLog!;
        sessionLog.Write("executor_evidence_validated", evidenceValidation);
        if (evidenceValidation.IsValid)
        {
            await CompactIfNeededAsync(
                model,
                _systemPrompt,
                sessionLog,
                streamProgress,
                cancellationToken);
        }
        var request = new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                isFinal
                    ? "[AI_HUB_FINAL_RESULT_REQUEST]"
                    : "[AI_HUB_RESULT_SNAPSHOT_REQUEST]",
                $"Current stage: {_currentStageId}",
                $"Latest compact available result: {_lastTurn?.CurrentResultSummary ?? string.Empty}",
                BuildTreeContextMessage(),
                _evidenceService.BuildEvidencePacket(
                    _actionGraph,
                    _evidenceReceipts,
                    evidenceValidation),
                "Technical tool payloads in earlier messages are internal evidence records. Never reproduce their JSON. Use only the normalized verified-result text in the evidence packet.",
                isFinal
                    ? "Create the complete final artifact from the confirmed active branch and all useful current work."
                    : "Execute the confirmed user task now and create the requested artifact itself from all confirmed data and current work.",
                "If the user requested an article, write the article. If the user requested a comparison, provide the comparison. If the user requested an answer, answer it.",
                "Never substitute a technical brief, task specification, content plan, outline, writing instructions or a promise of future work unless that is exactly what the user requested.",
                "Include important assumptions, limitations and sources when present.",
                isFinal
                    ? "Resolve available fragments into one coherent detailed document. Do not mention saving, exporting, snapshots, session controls or future refinement."
                    : "This is an on-demand preliminary version. Keep it useful even if some details remain open.",
                "Do not ask a question, do not end the session and do not describe future work.",
                "Return Markdown only. Start with one level-one heading.")
        };
        var snapshotMessages = _messages
            .Select(message => new StructuredChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                Name = message.Name,
                ToolCallId = message.ToolCallId,
                ToolCalls = message.ToolCalls
            })
            .Append(request)
            .ToList();
        sessionLog.Write(
            isFinal
                ? "executor_final_result_requested"
                : "executor_snapshot_requested",
            new
            {
                Stage = _currentStageId,
                NextVersion = _snapshotVersion + 1
            });
        var fallbackMarkdown = _evidenceService.BuildHonestLimitedMarkdown(
            _handoff!,
            _confirmedBriefCheckpoint,
            _actionGraph,
            _evidenceReceipts,
            evidenceValidation,
            GetWorkingResultFragments(),
            _lastTurn?.CurrentResultSummary);
        var markdown = _pendingSnapshotMarkdown;
        for (var attempt = 1; string.IsNullOrWhiteSpace(_pendingSnapshotMarkdown) && attempt <= 2; attempt++)
        {
            try
            {
                var response = await GenerateBackgroundExternalAsync(
                    model,
                    string.Join(
                        Environment.NewLine,
                        isFinal
                            ? BuildFinalResultSystemPrompt(_languageCode)
                            : BuildSnapshotSystemPrompt(_languageCode),
                        _restorationPrompt),
                    snapshotMessages,
                    [],
                    message => sessionLog.Write("executor_runtime", new { Message = message }),
                    streamProgress,
                    cancellationToken);
                markdown = response.Content.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sessionLog.Write("executor_snapshot_generation_failed", new
                {
                    Attempt = attempt,
                    ErrorType = ex.GetType().Name,
                    EvidenceStatus = evidenceValidation.Status
                });
                break;
            }

            var specificationWasRequested =
                ExecutorWorkingResultPolicy.TaskSpecificationWasRequested(_confirmedBriefCheckpoint);
            if (!ExecutorWorkingResultPolicy.LooksLikeTaskSpecification(markdown)
                || specificationWasRequested)
            {
                break;
            }

            sessionLog.Write("executor_snapshot_semantic_repair_requested", new
            {
                Attempt = attempt,
                Stage = _currentStageId
            });
            snapshotMessages.Add(new StructuredChatMessage
            {
                Role = "assistant",
                Content = markdown
            });
            snapshotMessages.Add(new StructuredChatMessage
            {
                Role = "user",
                Content = string.Join(
                    Environment.NewLine,
                    "[AI_HUB_RESULT_SEMANTIC_REPAIR]",
                    "The previous document was rejected because it described a technical brief, plan or instructions instead of performing the confirmed task.",
                    "Produce the requested artifact itself now. Preserve confirmed constraints, but do not describe them as a task specification.")
            });
        }

        if (string.IsNullOrWhiteSpace(markdown)
            || (ExecutorWorkingResultPolicy.LooksLikeTaskSpecification(markdown)
                && !ExecutorWorkingResultPolicy.TaskSpecificationWasRequested(
                    _confirmedBriefCheckpoint)))
        {
            markdown = fallbackMarkdown;
            sessionLog.Write("executor_snapshot_program_fallback_used", new
            {
                EvidenceStatus = evidenceValidation.Status,
                WorkingFragmentCount = GetWorkingResultFragments().Count
            });
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new InvalidOperationException("Executor returned an empty result snapshot.");
        }

        var version = ++_snapshotVersion;
        _pendingSnapshotMarkdown = markdown;
        _pendingSnapshotId = string.IsNullOrWhiteSpace(_pendingSnapshotId) ? $"snapshot_{version}_{Guid.NewGuid():N}" : _pendingSnapshotId;
        _snapshotVersion--; // The pending result has not been committed yet.
        PersistBackgroundCheckpoint();
        _snapshotVersion++;
        var snapshot = new ExecutorResultSnapshot
        {
            Id = _pendingSnapshotId,
            Version = version,
            CreatedAt = DateTimeOffset.Now,
            StageId = _currentStageId,
            Title = ExtractSnapshotTitle(markdown, version, _languageCode),
            Markdown = markdown,
            IsFinal = isFinal
        };
        var materialized = new SandboxArtifactMaterializerService().Materialize(
            snapshot,
            _handoff!,
            _sessionFileManifest,
            _storageSettings!,
            _evidenceReceipts);
        materialized.EvidenceValidation = evidenceValidation;
        materialized.TaskValidation = _evidenceService.ValidateTask(
            materialized.Validation,
            evidenceValidation,
            FirstNonEmpty(
                _confirmedBriefCheckpoint,
                _handoff!.Goal,
                _handoff.SuggestedDirection));
        snapshot.ArtifactPath = materialized.FilePath;
        snapshot.ArtifactKind = materialized.ArtifactKind;
        snapshot.ArtifactMimeType = materialized.MimeType;
        snapshot.ArtifactQualityLevel = materialized.QualityLevel;
        snapshot.ArtifactValidationStatus = materialized.Validation.Status;
        snapshot.EvidenceValidationStatus = evidenceValidation.Status;
        snapshot.TaskFulfillmentStatus = materialized.TaskValidation.Status;
        snapshot.ActionGraphId = _actionGraph.Id;
        snapshot.EvidenceReceiptIds = [.. evidenceValidation.ReceiptIds];
        snapshot.RecipeId = materialized.RecipeId;
        snapshot.ArtifactWarnings = materialized.Warnings
            .Concat(materialized.TaskValidation.Warnings)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        UpdateFinalActionStatuses(materialized);
        sessionLog.Write("executor_artifact_materialized", new
        {
            snapshot.Id,
            snapshot.Version,
            snapshot.ArtifactPath,
            snapshot.ArtifactKind,
            snapshot.ArtifactQualityLevel,
            snapshot.RecipeId
        });
        sessionLog.Write("executor_artifact_validated", materialized.Validation);
        sessionLog.Write("executor_task_fulfillment_validated", materialized.TaskValidation);
        if (!materialized.Validation.IsValid)
        {
            throw new InvalidDataException(
                "The executor output artifact failed validation: "
                + string.Join(" ", materialized.Validation.Errors));
        }

        _snapshots.Add(snapshot);
        _messages.Add(request);
        _messages.Add(new StructuredChatMessage
        {
            Role = "assistant",
            Content = string.Join(
                Environment.NewLine,
                isFinal
                    ? $"[AI_HUB_FINAL_RESULT id={snapshot.Id} version={snapshot.Version}]"
                    : $"[AI_HUB_RESULT_SNAPSHOT id={snapshot.Id} version={snapshot.Version}]",
                snapshot.Markdown)
        });
        sessionLog.Write(
            isFinal
                ? "executor_final_result_saved"
                : "executor_snapshot_saved",
            snapshot);
        _knowledgeTree.RecordSnapshot(snapshot);
        // Keep the receipt until the root has committed; a crash here must not create a second snapshot.
        PersistBackgroundCheckpoint();
        return snapshot;
    }

    private void UpdateFinalActionStatuses(SandboxArtifactMaterializationResult materialized)
    {
        var artifactNode = _actionGraph.Nodes.FirstOrDefault(node =>
            string.Equals(node.Layer, "artifact", StringComparison.Ordinal));
        if (artifactNode is not null)
        {
            artifactNode.Status = materialized.Validation.IsValid
                ? ExecutionActionStatuses.Succeeded
                : ExecutionActionStatuses.Failed;
        }

        var validationNode = _actionGraph.Nodes.FirstOrDefault(node =>
            string.Equals(node.Layer, "validation", StringComparison.Ordinal));
        if (validationNode is not null)
        {
            validationNode.Status = materialized.Validation.IsValid
                ? ExecutionActionStatuses.Succeeded
                : ExecutionActionStatuses.Failed;
        }
    }

    private ExecutorTurnResult CreateSafetyPause(
        string reason,
        string russianQuestion,
        string englishQuestion)
    {
        var russian = _languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        var turn = new ExecutorTurnResult
        {
            Status = ExecutorTurnStatuses.Working,
            Action = ExecutorTurnActions.AskUser,
            StageId = _currentStageId,
            StageSummary = BuildStageCheckpoint(_lastTurn),
            Thought = russian
                ? "Для продолжения требуется решение пользователя."
                : "A user decision is required before work can continue.",
            Question = russian ? russianQuestion : englishQuestion,
            Options = [],
            AllowCustom = true,
            CurrentResultSummary = _lastTurn?.CurrentResultSummary ?? string.Empty,
            WorkingResultFragment = string.Empty,
            Warnings = [reason]
        };
        _lastTurn = turn;
        _backgroundLoopPending = false;
        _knowledgeTree.RecordTurn(turn);
        PersistBackgroundCheckpoint();
        _sessionLog?.Write("executor_safety_pause", new
        {
            Stage = _currentStageId,
            Reason = reason
        });
        return turn;
    }

    internal ExecutorTurnResult CreateToolSafetyPause(string reason) =>
        CreateSafetyPause(
            reason,
            "Инструмент не смог завершить запрос. Уточните, как продолжить без него.",
            "The tool could not complete the request. Clarify how to continue without it.");

    private bool HasAvailableSessionFiles() =>
        _sessionFileManifest.Files.Any(file => file.IsAvailable);

    private static IReadOnlyList<string> GetReadyAdapterToolNames(
        ExecutorHandoffPackage handoff)
    {
        var route = handoff.ExecutionBundle.SelectedRouteLevel switch
        {
            ExecutionRouteLevels.Degraded =>
                handoff.ExecutionBundle.DegradedRoute.Route,
            ExecutionRouteLevels.Emergency =>
                handoff.ExecutionBundle.EmergencyRoute.Route,
            _ => handoff.ExecutionBundle.PreferredRoute.Route
        };
        return route.Resolution.Bindings
            .Where(binding => binding.IsExecutable)
            .SelectMany(binding => binding.ToolNames)
            .Where(ExecutorToolCatalog.IsAdapterTool)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void RefreshSessionFileTools()
    {
        _tools.RemoveAll(tool =>
            ExecutorToolCatalog.IsSessionFileTool(tool.Function.Name));
        if (!HasAvailableSessionFiles())
        {
            return;
        }

        _tools.AddRange(ExecutorToolCatalog.CreateDefinitions(
            includeWeb: false,
            includeSessionFiles: true));
    }

    private async Task<ExecutorTurnResult> RunLoopAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken,
        string? requiredToolName = null, string? requiredTargetId = null)
    {
        var model = _model ?? throw new InvalidOperationException("Executor model is unavailable.");
        var storageSettings = _storageSettings ?? throw new InvalidOperationException("Executor storage is unavailable.");
        var sessionLog = _sessionLog ?? throw new InvalidOperationException("Executor log is unavailable.");
        if (!_backgroundLoopPending)
        {
            _backgroundLoopPending = true;
            _pendingRequiredTool = requiredToolName;
            _pendingRequiredTarget = requiredTargetId;
            _pendingRequiredToolSatisfied = string.IsNullOrWhiteSpace(requiredToolName);
        }
        requiredToolName = _pendingRequiredTool;
        var requiredToolSatisfied = _pendingRequiredToolSatisfied;
        PersistBackgroundCheckpoint();
        await ExecutePendingBackgroundToolsAsync(cancellationToken);
        requiredToolSatisfied = _pendingRequiredToolSatisfied;
        var budget = new AutonomyExecutionBudget(_autonomySeconds);
        if (!requiredToolSatisfied)
        {
            budget.Start();
        }
        var round = 0;
        while (budget.CanStartNext(round == 0))
        {
            round++;
            cancellationToken.ThrowIfCancellationRequested();
            await CompactIfNeededAsync(model, _systemPrompt, sessionLog, streamProgress, cancellationToken);
            var response = await GenerateBackgroundExternalAsync(
                model,
                _systemPrompt,
                _messages,
                _tools,
                message => sessionLog.Write("executor_runtime", new { Message = message }),
                streamProgress,
                cancellationToken,
                responseFormat: null,
                requiredToolName: round == 1 ? requiredToolName : null);
            sessionLog.Write("executor_model_response", new
            {
                Round = round,
                response.Content,
                response.FinishReason,
                response.ToolCalls
            });
            if (!response.HasToolCalls)
            {
                if (!requiredToolSatisfied)
                {
                    sessionLog.Write("executor_required_tool_missing", new
                    {
                        RequiredTool = requiredToolName,
                        Round = round
                    });
                    throw new InvalidOperationException(
                        $"Executor did not call the required tool '{requiredToolName}'.");
                }

                _messages.Add(new StructuredChatMessage { Role = "assistant", Content = response.Content });
                var rejectionReason = "json_parse_or_schema_failure";
                ExecutorTurnResult? rejectedTurn = null;
                if (ExecutorResultParser.TryReadTurn(response.Content, out var turn))
                {
                    if (IsTurnAllowedInCurrentStage(turn))
                    {
                        return AcceptTurn(turn, sessionLog, "executor_turn");
                    }

                    rejectedTurn = turn;
                    rejectionReason = WriteStagePolicyRejection(
                        sessionLog,
                        turn,
                        "executor_turn");
                }

                sessionLog.Write("executor_contract_repair_requested", new
                {
                    RawResponse = response.Content,
                    RejectionReason = rejectionReason
                });
                var attempt = 0;
                budget.Start();
                budget.RegisterProgress(rejectedTurn is null
                    ? BuildRawRepairFingerprint(response.Content)
                    : BuildTurnRepairFingerprint(rejectedTurn));
                while (budget.CanStartNext())
                {
                    attempt++;
                    _messages.Add(new StructuredChatMessage
                    {
                        Role = "user",
                        Content = BuildContractRepairMessage(
                            _currentStageId,
                            rejectionReason)
                    });
                    var repaired = await GenerateBackgroundExternalAsync(
                        model,
                        _systemPrompt,
                        _messages,
                        [],
                        message => sessionLog.Write("executor_runtime", new { Message = message }),
                        streamProgress,
                        cancellationToken,
                        ExecutorJsonContract.CreateResponseFormat());
                    _messages.Add(new StructuredChatMessage { Role = "assistant", Content = repaired.Content });
                    sessionLog.Write("executor_contract_repair_response", new
                    {
                        Attempt = attempt,
                        repaired.Content,
                        repaired.FinishReason
                    });
                    if (ExecutorResultParser.TryReadTurn(repaired.Content, out turn))
                    {
                        if (IsTurnAllowedInCurrentStage(turn))
                        {
                            return AcceptTurn(turn, sessionLog, "executor_contract_repaired");
                        }

                        rejectedTurn = turn;
                        rejectionReason = WriteStagePolicyRejection(
                            sessionLog,
                            turn,
                            "executor_contract_repaired");
                    }
                    else
                    {
                        rejectedTurn = null;
                        rejectionReason = "json_parse_or_schema_failure";
                    }

                    if (!budget.RegisterProgress(
                            rejectedTurn is null
                                ? BuildRawRepairFingerprint(repaired.Content)
                                : BuildTurnRepairFingerprint(rejectedTurn)))
                    {
                        sessionLog.Write("executor_contract_repair_stagnation", new
                        {
                            Attempt = attempt,
                            Stage = _currentStageId
                        });
                        break;
                    }
                }

                sessionLog.Write("executor_contract_repair_failed", new
                {
                    Attempts = attempt,
                    Stage = _currentStageId,
                    RejectionReason = rejectionReason,
                    ElapsedMilliseconds = budget.Elapsed.TotalMilliseconds,
                    LimitMilliseconds = budget.Limit.TotalMilliseconds
                });
                return CreateSafetyPause(
                    "contract_repair_time_or_stagnation",
                    BuildRepairSafetyMessage(rejectionReason, russian: true),
                    BuildRepairSafetyMessage(rejectionReason, russian: false));
            }

            budget.Start();
            var toolFingerprint = string.Join(
                "|",
                response.ToolCalls.Select(toolCall =>
                    $"{toolCall.Function.Name}:{toolCall.Function.Arguments}"));
            if (!budget.RegisterProgress(toolFingerprint))
            {
                sessionLog.Write("executor_tool_stagnation", new
                {
                    Round = round,
                    Fingerprint = toolFingerprint
                });
                return CreateToolSafetyPause("repeated_tool_call_without_progress");
            }

            // Backends may reuse call IDs on a new generation. Receipts must still identify one execution.
            var knownCallIds = _messages.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id)
                .Concat(_messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId)).ToHashSet(StringComparer.Ordinal);
            foreach (var call in response.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || !knownCallIds.Add(call.Id))
                    call.Id = "call_" + Guid.NewGuid().ToString("N");
                knownCallIds.Add(call.Id);
            }
            _messages.Add(new StructuredChatMessage
            {
                Role = "assistant",
                Content = response.Content,
                ToolCalls = response.ToolCalls
            });
            _pendingToolCalls.AddRange(Clone(response.ToolCalls));
            PersistBackgroundCheckpoint();
            await ExecutePendingBackgroundToolsAsync(cancellationToken);
            requiredToolSatisfied = _pendingRequiredToolSatisfied;

            if (!_evidenceProgressGuard.Observe(_actionGraph))
            {
                sessionLog.Write("executor_evidence_stagnation", new
                {
                    Round = round,
                    RequiredActions = _actionGraph.Nodes
                        .Where(ExecutionActionGraphService.IsOperational)
                        .Where(node => node.Required)
                        .Select(node => new
                        {
                            node.Id,
                            node.OutcomeActionId,
                            node.CapabilityId,
                            node.Status
                        })
                        .ToList()
                });
                return CreateToolSafetyPause("repeated_evidence_without_progress");
            }
        }

        sessionLog.Write("executor_autonomy_time_budget_reached", new
        {
            Round = round,
            ElapsedMilliseconds = budget.Elapsed.TotalMilliseconds,
            LimitMilliseconds = budget.Limit.TotalMilliseconds
        });
        return CreateToolSafetyPause("autonomy_time_budget");
    }

    private string? GetRequiredFileEvidenceTool()
    {
        if (!HasAvailableSessionFiles()
            || string.IsNullOrWhiteSpace(_confirmedBriefCheckpoint))
        {
            return null;
        }

        var unreadRequiredFile = _sessionFileManifest.Files
            .Where(file => file.IsAvailable && IsTextualFileCategory(file.Category))
            .FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.DisplayName)
                && _confirmedBriefCheckpoint.Contains(
                    file.DisplayName,
                    StringComparison.OrdinalIgnoreCase)
                && !_successfulToolCalls.Contains(
                    CreateToolEvidenceKey("session_file_read", file.Id)));
        return unreadRequiredFile is not null ? "session_file_read" : null;
    }

    private static bool IsTextualFileCategory(string category) =>
        category is SessionFileCategories.Document
            or SessionFileCategories.Table
            or SessionFileCategories.Code
            or SessionFileCategories.Text;

    private static string CreateToolEvidenceKey(string toolName, string targetId) =>
        string.IsNullOrWhiteSpace(targetId)
            ? toolName
            : $"{toolName}:{targetId}";

    private static string TryReadToolTargetId(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.TryGetProperty("file_id", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private ExecutorTurnResult AcceptTurn(
        ExecutorTurnResult turn,
        ISessionEventLog sessionLog,
        string eventType)
    {
        turn.StageId = _currentStageId;
        turn.CurrentResultSummary = ExecutorResultSummaryPolicy.Clamp(turn.CurrentResultSummary);
        turn.WorkingResultFragment = ExecutorWorkingResultPolicy.Clamp(turn.WorkingResultFragment);
        if (_briefConfirmed
            && ExecutorWorkingResultPolicy.LooksLikeMetaDescription(turn.CurrentResultSummary)
            && ExecutorWorkingResultPolicy.IsSubstantive(turn.WorkingResultFragment))
        {
            turn.CurrentResultSummary = ExecutorResultSummaryPolicy.Clamp(turn.WorkingResultFragment);
            sessionLog.Write("executor_result_summary_replaced_from_fragment", new
            {
                Stage = _currentStageId
            });
        }

        _lastTurn = turn;
        _backgroundLoopPending = false;
        _knowledgeTree.RecordTurn(turn);
        PersistBackgroundCheckpoint();
        sessionLog.Write(eventType, turn);
        if (turn.CanFinalize)
        {
            sessionLog.Write(
                turn.Action == ExecutorTurnActions.SuggestFinalization
                    ? "executor_finalization_suggested"
                    : "executor_finalization_available",
                new
                {
                    Stage = _currentStageId,
                    turn.Action,
                    turn.CompletionReason
                });
        }

        if (!string.IsNullOrWhiteSpace(turn.CurrentResultSummary))
        {
            sessionLog.Write("executor_result_summary_updated", new
            {
                Stage = _currentStageId,
                Summary = turn.CurrentResultSummary
            });
        }

        return turn;
    }

    private bool IsTurnAllowedInCurrentStage(ExecutorTurnResult turn)
    {
        return ExecutorTurnStagePolicy.IsAllowed(turn, _currentStageId, _briefConfirmed);
    }

    private string WriteStagePolicyRejection(
        ISessionEventLog sessionLog,
        ExecutorTurnResult turn,
        string source)
    {
        var reason = ExecutorTurnStagePolicy.GetRejectionReason(
            turn,
            _currentStageId,
            _briefConfirmed) ?? "unknown_stage_policy_rejection";
        sessionLog.Write("executor_turn_rejected_by_stage_policy", new
        {
            Source = source,
            CurrentStage = _currentStageId,
            BriefConfirmed = _briefConfirmed,
            Reason = reason,
            turn.Status,
            turn.Action,
            HasCurrentResultSummary = !string.IsNullOrWhiteSpace(turn.CurrentResultSummary),
            HasSubstantiveWorkingResultFragment =
                ExecutorWorkingResultPolicy.IsSubstantive(turn.WorkingResultFragment),
            turn.CanFinalize,
            HasCompletionReason = !string.IsNullOrWhiteSpace(turn.CompletionReason),
            RequestedCapabilities = turn.RequestedCapabilities.Select(request => request.Id)
        });
        return reason;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Dispose();
    }

    private async Task CompactIfNeededAsync(
        DebugModelInfo model,
        string systemPrompt,
        ISessionEventLog sessionLog,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
    {
        var budget = ExecutorContextBudgetManager.Measure(
            _messages.Select(message => message.Content ?? string.Empty),
            CoreContextRuntimeLimits.CurrentBackendContextLimit);
        sessionLog.Write("executor_context_budget", budget);
        if (!budget.ShouldCompact || _messages.Count < 3)
        {
            return;
        }

        var transcript = string.Join(Environment.NewLine, _messages.Select(message => $"[{message.Role}] {message.Content}"));
        var checkpoint = await _runtime.GenerateExecutorAsync(
            model,
            string.Join(
                Environment.NewLine,
                "Create a compact, factual execution checkpoint.",
                $"The current user-controlled stage is {_currentStageId}; do not change it.",
                $"The task brief is {(_briefConfirmed ? "confirmed" : "not confirmed")}.",
                "Preserve the goal, decisions, confirmed data, evidence, sources, tool results, workingResultFragment values, full result snapshots, current result versions, the latest currentResultSummary, unfinished questions and risks.",
                "Keep provisional core hypotheses marked as provisional. Do not solve the task again."),
            transcript,
            message => sessionLog.Write("executor_runtime", new { Message = message }),
            streamProgress,
            cancellationToken);
        _messages.Clear();
        _messages.Add(new StructuredChatMessage
        {
            Role = "user",
            Content = string.Join(
                Environment.NewLine,
                "Continue the original session from this verified checkpoint:",
                checkpoint,
                BuildStageInstruction(_currentStageId),
                $"The task brief is {(_briefConfirmed ? "confirmed" : "not confirmed")}.",
                $"Latest compact available result: {_lastTurn?.CurrentResultSummary ?? string.Empty}",
                BuildTreeContextMessage(),
                "The session remains open until the user presses the program's Finish session button.")
        });
        sessionLog.Write("executor_context_checkpoint", new
        {
            Stage = _currentStageId,
            Checkpoint = checkpoint
        });
    }

    private static string BuildSystemPrompt(ExecutorHandoffPackage handoff) =>
        string.Join(
            Environment.NewLine,
            "You are the selected AI executor in the AI HUB Sandbox scenario, not the AI HUB core.",
            "The core selected your capability class. It did not discover the user's exact subject and did not prepare a final task.",
            "Treat programFacts as authoritative, userSignals as raw user input, and coreHypotheses only as provisional background that may be wrong.",
            "workPatterns contains the Sandbox core's provisional pattern classification with conditional match percentages. Use it as routing context, not as proof.",
            "artifactContract is authoritative for the required output kind, preferred format and minimum emergency result. Do not silently replace it with a text explanation.",
            "outcomeContract is the program-owned statement of what must actually happen. A required outcome action is complete only when its expected evidence type is present in an AI HUB receipt.",
            "Coordinator reasoning, a route percentage, an installed package, metadata, or a plausible narrative never substitutes for evidence of a required outcome action.",
            $"[AI_HUB_EXECUTION_OUTCOME_CONTRACT]{JsonSerializer.Serialize(handoff.OutcomeContract)}",
            "executionBundle describes preferred, degraded and emergency routes. Continue through the selected route and request missing capabilities only when they materially improve or enable the artifact.",
            "Recipes are executable intentions owned by AI HUB. Choose the best ready recipe, but never claim that a deterministic step ran unless AI HUB exposed and completed the corresponding tool.",
            "fileManifest contains only trusted file names and metadata selected by the user. It never contains file contents or absolute paths.",
            "When fileManifest.contentAccessAvailable is true, supported content is available only through session_files_list, session_file_inspect and session_file_read. Never infer content from a name or metadata.",
            "When fileManifest.contentAccessAvailable is false, never claim that you read, opened, saw, transcribed or analyzed a file.",
            "Use session_files_list when file IDs or the current set are uncertain. Inspect an unfamiliar or large file before reading it.",
            "Use session_file_read in bounded chunks. Start at offset 0 and continue only with next_offset when more content is relevant.",
            "A successful file tool result is evidence, not a user answer. After reading, apply that evidence to the actual task and produce a substantive working result.",
            "Do not ask the user to manually retell a supported text or document file before trying the safe file tools.",
            "The file tools never provide semantic image, audio or video understanding. Technical dimensions or metadata do not mean that you saw, heard or watched the media.",
            "If an adapter is unavailable, do not fabricate access. Request one approved capability after brief confirmation, ask for a safe fallback, or explain the limitation.",
            "Use file categories to identify required capabilities and to ask what role the files should play when that role is unclear.",
            "An [AI_HUB_FILE_MANIFEST_UPDATED] event is context, not an answer to the current question.",
            "A newly added file can be primary task input, a separate example/reference, or explanatory context. If its role is unclear, ask specifically about that role instead of assuming it belongs to the main input set.",
            "When asking about a file role, always provide ready selectable options for primary task input, separate example/reference, explanatory context, and do not use. Custom input is only a fallback.",
            "The work has exactly two AI HUB controlled stages: task_definition and practical_clarification.",
            "Never change the stage yourself. Work only inside the current stage named by AI HUB.",
            "Only an explicit [AI_HUB_STAGE_CHANGE] message changes task_definition to practical_clarification. practical_clarification is permanent until the user ends the session.",
            "Ask exactly one useful decision at a time. Do not run a silent autonomous content loop and do not decide that enough information has been collected.",
            "When asking, offer 2-6 short options and allow a custom answer. Every option is an object with title, intent, action, targetId, effect and isRecommended.",
            "Use intent answer for ordinary answers and intent decline_action for a clear refusal. Leave action and targetId empty for both.",
            "Use intent approve_action only when the user must explicitly approve a concrete safe file action. Set action to session_files_list, session_file_inspect or session_file_read, targetId to the trusted manifest file ID when applicable, and effect to a short honest description of what AI HUB will do.",
            "An approve_action title must be a concrete command such as Read this file, not a vague Yes. Do not create an approve_action for work that is not backed by one of the allowed tools.",
            "Do not ask permission for safe file reading when the confirmed user task already clearly requires that file. Call the tool directly instead.",
            "Do not output duplicate or near-duplicate options. Each option must lead to a materially different answer or action.",
            "Offer equivalents of Not important, Decide yourself, or Skip only when they are genuinely safe.",
            "Never offer stage names, transition commands, result commands or session commands as answer options.",
            "Actions: ask_user waits for the user; confirm_brief asks AI HUB to show the completed technical brief; request_tool asks for safe web tools; request_capability asks AI HUB for one or more task capabilities; suggest_finalization recommends a user-controlled finish; blocked explains why work cannot continue.",
            "Use confirm_brief only with status stage_ready in task_definition. Put the complete actionable task formulation in stageSummary and leave options empty.",
            "After task confirmation, an ordinary response returns working + ask_user, one practical question, a substantive workingResultFragment, and an updated currentResultSummary.",
            $"workingResultFragment is new content that directly performs part of the confirmed task. Keep it within {ExecutorWorkingResultPolicy.MaximumCharacters} characters.",
            "A workingResultFragment must contain the answer itself: facts, analysis, prose, comparison, recommendation, calculation or another requested artifact fragment. It must not be a plan, technical brief, task specification, production instruction or promise.",
            $"currentResultSummary is a concise retelling derived from the actual answer fragments available right now. Keep it within {ExecutorResultSummaryPolicy.MaximumCharacters} characters.",
            "Prioritize present answer content, key recommendations, important caveats and gaps that materially affect it. Never write that an answer is being prepared or will be created later.",
            "Use request_tool only after the task is confirmed and list only web_search, web_research or web_read in requestedTools.",
            $"Use request_capability at any stage when a capability is needed to inspect or understand an input, form an actionable task brief, or continue the confirmed task. Put every simultaneously required capability into requestedCapabilities (maximum 8), with a plain task-specific purpose, required flag and possible capability-ID alternatives. Known IDs include: {string.Join(", ", ComponentCatalog.Processing.SelectMany(entry => entry.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))}. You may request an unknown but precise capability when the trusted catalog has no match; AI HUB will decide whether external discovery is allowed. A capability request is an internal action, never an answer option. After AI HUB reports the capability outcome, resume the same stage with the preserved context. Never name a package, executable, command or URL.",
            "Fill missingCriticalInputs and assumptions honestly; do not invent a precise readiness percentage.",
            "Set canFinalize true as soon as the active branch contains a useful result that can be delivered now, missingCriticalInputs is empty, and the next question would only improve, expand or polish that result. You may still use ask_user with canFinalize true for one genuinely useful optional question.",
            "When canFinalize is true, explain why the current result is already usable in completionReason. Otherwise set canFinalize false and leave completionReason empty.",
            "Use suggest_finalization only when the current result is usable and no meaningful optional question remains. For suggest_finalization set canFinalize true, leave question and options empty, set allowCustom false, and include the latest substantive workingResultFragment and currentResultSummary.",
            "suggest_finalization is only advice to AI HUB. It does not save, export, show a result or end the session.",
            "Statuses: working asks the next question, requests a tool or recommends finalization; stage_ready is only for the first brief confirmation; blocked explains what prevents progress.",
            "The session never ends from your response. A full result is created only by a separate AI HUB snapshot request initiated by the user.",
            "Keep result empty for working and stage_ready. Update stageSummary with the technical checkpoint and workingResultFragment with actual useful output. Never mix these roles.",
            "Use only tools exposed by the application. Never claim that an unavailable tool was used.",
            "Every non-tool response must follow the JSON response schema. Never return continue_work, present_result, result_ready, final_result, completed, session_ended, or another terminal status.",
            "Keep thought to one short user-facing sentence. It is not hidden chain-of-thought.",
            $"Answer language: {handoff.LanguageCode}.");

    private static string BuildActionGraphPrompt(ExecutionActionGraph graph) =>
        string.Join(
            Environment.NewLine,
            "[AI_HUB_EXECUTION_ACTION_GRAPH]",
            "This program-owned graph lists the operations and evidence required for the current task route.",
            "Use the exposed tool names assigned to graph nodes whenever they are available and relevant.",
            "A capability name, route card, model statement or planned action is not proof that an operation ran.",
            "Only a successful AI HUB tool execution receipt may support a claim that a deterministic operation completed or that an input was inspected.",
            "Never invent file content, objects, people, transcription, measurements, calculations, edits, exports or web findings that are absent from tool evidence or confirmed user input.",
            "When required evidence cannot be obtained, continue with an explicitly limited result based only on available facts and name the missing capability. Do not fill evidence gaps with plausible details.",
            JsonSerializer.Serialize(graph));

    private static string BuildUserPrompt(ExecutorHandoffPackage handoff, string stageId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Handoff package with provenance:");
        builder.AppendLine(JsonSerializer.Serialize(handoff));
        builder.AppendLine();
        builder.AppendLine("Important: Goal and Executor prompt are provisional core hypotheses, not a confirmed user request.");
        builder.AppendLine($"Required artifact: {handoff.ArtifactContract.ArtifactKind} {handoff.ArtifactContract.PreferredExtension} ({handoff.ArtifactContract.MimeType}).");
        builder.AppendLine($"Selected route: {handoff.ExecutionBundle.SelectedRouteLevel}.");
        builder.AppendLine("The session must ultimately produce and validate a physical artifact matching artifactContract. A route card or limitation message is not the artifact.");
        builder.AppendLine(BuildStageInstruction(stageId));
        builder.AppendLine("Begin with status working and action ask_user. Ask the first broad technical question. Keep currentResultSummary and workingResultFragment empty until the brief is confirmed.");
        return builder.ToString();
    }

    private static string BuildStageInstruction(string stageId) => stageId switch
    {
        ExecutorStageIds.TaskDefinition =>
            "Current stage task_definition: clarify the real subject, actionable desired outcome, audience, constraints and success criteria. Ask only critical non-repeating technical questions. Keep currentResultSummary and workingResultFragment empty, set canFinalize false and leave completionReason empty. When the task is actionable, return status stage_ready and action confirm_brief with the complete brief in stageSummary, no transition options, and no result.",
        ExecutorStageIds.PracticalClarification =>
            $"Current stage practical_clarification: stay in this stage. Progressively perform the confirmed task while asking practical questions about approach, missing data, preferences, edge cases, risks and validation. An ordinary turn returns working + ask_user with exactly one useful question, a substantive new workingResultFragment of at most {ExecutorWorkingResultPolicy.MaximumCharacters} characters, and an updated currentResultSummary of at most {ExecutorResultSummaryPolicy.MaximumCharacters} characters derived from actual answer content. Build useful answer fragments, not a full final document, technical brief, outline or promise. Set canFinalize true when the current branch already contains a useful deliverable and there are no critical missing inputs, even if one optional improvement question remains; explain that readiness in completionReason. Keep canFinalize false and completionReason empty while the answer would still be materially incomplete. Use working + suggest_finalization only when the result is usable and no meaningful optional question remains. This recommends the program's finish flow but never performs it.",
        _ => throw new ArgumentOutOfRangeException(nameof(stageId), stageId, "Unknown executor stage.")
    };

    private string BuildContractRepairMessage(
        string stageId,
        string rejectionReason) =>
        string.Join(
            Environment.NewLine,
            "[AI_HUB_CONTRACT_REPAIR]",
            $"Rejection class: {rejectionReason}.",
            rejectionReason == "json_parse_or_schema_failure"
                ? "Your previous response could not be parsed against the JSON response contract. Return one complete schema-valid JSON object."
                : "Your previous JSON was parsed, but it violated the semantic policy of the current stage. Preserve valid content and correct the named policy violation.",
            rejectionReason == "working_result_fragment_missing"
                ? "The rejected practical turn had no new substantive workingResultFragment. Add actual answer content now, or use suggest_finalization with a substantive fragment when the saved result is already self-contained."
                : string.Empty,
            BuildStageInstruction(stageId),
            "Return JSON only using working, stage_ready or blocked.",
            "Return one action: ask_user, confirm_brief, request_tool, request_capability, suggest_finalization or blocked.",
            _briefConfirmed
                ? $"The task brief is already confirmed. Return working + ask_user with one useful practical question, or working + suggest_finalization when no useful question remains. In both cases include a substantive workingResultFragment of at most {ExecutorWorkingResultPolicy.MaximumCharacters} characters and a useful currentResultSummary of at most {ExecutorResultSummaryPolicy.MaximumCharacters} characters. Set canFinalize true with a non-empty completionReason whenever that current result can already be delivered and missingCriticalInputs is empty, including ask_user turns with only optional improvements left. Otherwise set canFinalize false and leave completionReason empty. Write actual answer content, not a technical brief or future plan."
                : "The task brief is not confirmed. Use working + request_capability when a capability is required to inspect an input or make task_definition actionable. Otherwise use confirm_brief only when task_definition is already actionable.",
            "If the rejected response contained valid requestedCapabilities, preserve them and return working + request_capability. Capability requests are valid before and after task confirmation.",
            "For request_capability leave question and options empty. A capability request is handled by AI HUB and must never be represented as an approve_action answer option.",
            "Never put stage transitions, saving, exporting, result display or session commands into options. Use suggest_finalization instead when appropriate.",
            "Options are structured objects. Use approve_action only for a concrete session file tool and include its action, trusted targetId and effect. Use answer or decline_action for everything else.",
            "Remove duplicate or equivalent options. A green action in AI HUB must represent a real tool call, never a decorative recommendation.",
            "Never return continue_work, present_result or result_ready.",
            "Keep the session open. Do not change stages and do not claim that the session ended.");

    internal static string BuildTurnRepairFingerprint(ExecutorTurnResult turn) =>
        string.Join(
            "|",
            "semantic_turn",
            NormalizeRepairValue(turn.Status),
            NormalizeRepairValue(turn.Action),
            NormalizeRepairValue(turn.Question),
            NormalizeRepairValue(turn.WorkingResultFragment),
            turn.CanFinalize ? "ready" : "not_ready",
            NormalizeRepairValue(turn.CompletionReason));

    private static string BuildRawRepairFingerprint(string content) =>
        $"raw:{content.Length}:{StringComparer.Ordinal.GetHashCode(content)}";

    private static string NormalizeRepairValue(string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();

    private static string BuildRepairSafetyMessage(string reason, bool russian)
    {
        if (reason == "working_result_fragment_missing")
        {
            return russian
                ? "Исполнитель повторил практический ответ без нового содержательного фрагмента результата. Последняя принятая работа сохранена; повторите шаг или завершите сессию текущим результатом."
                : "The executor repeated a practical answer without a new substantive result fragment. The last accepted work is preserved; retry the step or finish with the current result.";
        }

        if (reason == "json_parse_or_schema_failure")
        {
            return russian
                ? "Исполнитель не смог вернуть ответ по требуемой JSON-схеме за отведённое время. Последняя принятая работа сохранена; повторите шаг."
                : "The executor could not return a response matching the required JSON schema within the available time. The last accepted work is preserved; retry the step.";
        }

        return russian
            ? "Исполнитель повторил ответ, не соответствующий требованиям текущего этапа. Последняя принятая работа сохранена; повторите шаг."
            : "The executor repeated a response that did not meet the current stage requirements. The last accepted work is preserved; retry the step.";
    }

    private static string BuildStageCheckpoint(ExecutorTurnResult? turn)
    {
        if (turn is null)
        {
            return "No executor turn has been completed in this stage yet.";
        }

        if (!string.IsNullOrWhiteSpace(turn.StageSummary))
        {
            return Shorten(turn.StageSummary, 1200);
        }

        var parts = new[] { turn.CurrentResultSummary, turn.Thought, turn.Question, turn.Result }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim());
        var fallback = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(fallback)
            ? "The user changed the stage before a summary was available."
            : Shorten(fallback, 1200);
    }

    private static string Shorten(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "...";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;

    private static string BuildSnapshotSystemPrompt(string languageCode) =>
        string.Join(
            Environment.NewLine,
            "You are producing an on-demand preliminary document from an active executor session.",
            "Execute the confirmed user task now. Use all confirmed facts, decisions, tool results, workingResultFragment values, the latest currentResultSummary and current work available in the conversation.",
            "Return the requested artifact itself. Never substitute a task specification, technical brief, outline, content plan, writing instructions or future-work promise unless the user explicitly requested that artifact.",
            "Do not ask questions, do not output JSON, do not end the session and do not claim unsupported facts.",
            "If important information is missing, state the limitation and still provide the most useful current document.",
            "Return clean Markdown with one level-one title, useful sections, paragraphs, lists and code blocks when appropriate.",
            "The document may be long. Do not replace it with a plan or a promise.",
            "Creating this document does not complete the session. The executor will continue practical clarification afterwards.",
            $"Document language: {languageCode}.");

    private static string BuildFinalResultSystemPrompt(string languageCode) =>
        string.Join(
            Environment.NewLine,
            "You are producing the final user-requested document from an executor session that the user chose to finish.",
            "Perform the confirmed task itself using the active knowledge-tree branch, confirmed decisions, useful working fragments, tool results, sources and stated limitations.",
            "Create one coherent, detailed and self-contained artifact. Resolve repetition and contradictions in favor of the latest confirmed active-branch decision.",
            "Never return a task specification, outline, production plan, promise, readiness message or instructions for another author unless that artifact was explicitly requested.",
            "Do not ask questions, output JSON, mention AI HUB controls, saving, exporting, snapshots or future work.",
            "If information remains uncertain, state the limitation inside the result without replacing the result.",
            "Return clean Markdown with one level-one title, useful sections, paragraphs, lists, tables or code blocks when appropriate.",
            "The document may be long. Preserve substantive detail instead of summarizing it away.",
            $"Document language: {languageCode}.");

    private static string BuildRestorationPrompt(SessionRestorationContext restoration) =>
        string.Join(
            Environment.NewLine,
            "[AI_HUB_SESSION_RESTORED]",
            $"Stable session id: {restoration.SessionId}.",
            $"Current restored run id: {restoration.RunId}.",
            $"Resume count: {restoration.ResumeCount}.",
            $"Original session created at: {restoration.OriginalCreatedAt:O}.",
            $"Restored at: {restoration.RestoredAt:O}.",
            $"Previous stop kind: {restoration.PreviousStopKind}.",
            $"Previous stop reason: {restoration.PreviousStopReason}.",
            $"Last stable stage: {restoration.LastStableStage}.",
            $"An uncommitted interrupted turn was lost: {restoration.LostUncommittedTurn}.",
            "This is a restored run of an existing session, not the original run and not a new task.",
            "Continue from the saved confirmed checkpoint. Do not restart discovery, repeat introductions or claim uninterrupted process memory.",
            "Treat previous results as preserved versions and a basis for continued work, not as immutable terminal answers.",
            restoration.LostUncommittedTurn
                ? "Do not assume that the interrupted unfinished action completed."
                : "All restored turns in the checkpoint were fully committed by AI HUB.",
            "Mention restoration to the user only when it materially affects the next decision.");

    private static string ExtractSnapshotTitle(string markdown, int version, string languageCode)
    {
        var title = markdown
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title[2..].Trim();
        }

        return languageCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            ? $"Текущий результат {version}"
            : $"Current result {version}";
    }

    private string BuildTreeContextMessage() =>
        string.Join(
            Environment.NewLine,
            "[AI_HUB_SESSION_TREE_ACTIVE_CONTEXT]",
            _knowledgeTree.BuildModelContext(),
            "This program-owned context is authoritative for confirmed decisions. Do not rewrite its structure or revive inactive alternatives.");

    private IReadOnlyList<string> GetWorkingResultFragments() =>
        _knowledgeTree.GetSnapshot().Nodes
            .Where(node => !node.IsStructural
                && string.Equals(
                    node.Type,
                    SessionKnowledgeNodeTypes.ResultFragment,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(node.Content))
            .OrderBy(node => node.Sequence)
            .TakeLast(8)
            .Select(node => node.Content.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void KnowledgeTree_Changed(
        object? sender,
        SessionKnowledgeTreeChangedEventArgs e)
    {
        var node = e.Snapshot.Nodes.FirstOrDefault(item =>
            string.Equals(item.Id, e.NodeId, StringComparison.Ordinal));
        _sessionLog?.Write("executor_tree_changed", new
        {
            e.ChangeType,
            e.NodeId,
            e.Snapshot.Version,
            Node = node
        });
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_model is null || _storageSettings is null || _sessionLog is null)
        {
            throw new InvalidOperationException("Executor session has not been started.");
        }
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Cannot clone {typeof(T).Name}.");
    }
}
