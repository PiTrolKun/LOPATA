using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ExecutorSessionService
{
    private bool _backgroundLoopPending;
    private string? _pendingRequiredTool;
    private string? _pendingRequiredTarget;
    private bool _pendingRequiredToolSatisfied;
    private readonly List<StructuredToolCall> _pendingToolCalls = [];
    private string _inFlightToolCallId = "";
    private string _pendingSnapshotMarkdown = "";
    private string _pendingSnapshotId = "";
    internal Action? BackgroundCheckpoint { get; set; }
    internal Func<IReadOnlyList<StructuredChatMessage>, CancellationToken, Task<StructuredChatResult>>? BackgroundGeneration { get; set; }
    internal Func<StructuredToolCall, CancellationToken, Task<ExecutorToolExecution>>? BackgroundTool { get; set; }
    internal SessionFileManifest BackgroundFileManifest => Clone(_sessionFileManifest);
    private void PersistBackgroundCheckpoint() => BackgroundCheckpoint?.Invoke();
    internal void BeginBackgroundSnapshot() { _pendingSnapshotMarkdown = ""; _pendingSnapshotId = ""; }

    private static bool IsReadOnlyBackgroundTool(string name) => name is "session_files_list" or "session_file_inspect" or "session_file_read"
        or "session_image_inspect_pixels" or "session_image_inspect_extended" or "session_image_describe"
        or "session_image_extract_text" or "session_audio_transcribe";

    private Task<StructuredChatResult> GenerateBackgroundExternalAsync(DebugModelInfo model, string system,
        IReadOnlyList<StructuredChatMessage> messages, IReadOnlyList<StructuredToolDefinition> tools,
        Action<string> log, IProgress<ModelStreamChunk> stream, CancellationToken token,
        JsonObject? responseFormat = null, string? requiredToolName = null)
        => BackgroundGeneration is null
            ? _runtime.GenerateExternalWithToolsAsync(model, system, messages, tools, log, stream, token, responseFormat, requiredToolName)
            : BackgroundGeneration(messages, token);

    internal Task<ExecutorTurnResult> ResumeBackgroundTurnAsync(IProgress<ModelStreamChunk> stream, CancellationToken token)
        => _backgroundLoopPending ? RunLoopAsync(stream, token, _pendingRequiredTool, _pendingRequiredTarget)
            : Task.FromResult(_lastTurn ?? throw new InvalidOperationException("No confirmed executor turn."));

    private async Task ExecutePendingBackgroundToolsAsync(CancellationToken token)
    {
        foreach (var call in _pendingToolCalls.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (_messages.Any(m => m.Role == "tool" && m.ToolCallId == call.Id))
            { _pendingToolCalls.Remove(call); PersistBackgroundCheckpoint(); continue; }
            if (_inFlightToolCallId == call.Id && !IsReadOnlyBackgroundTool(call.Function.Name))
                throw new BackgroundOperationWaitingException("Tray.ToolOutcomeUnknown");
            _inFlightToolCallId = call.Id;
            PersistBackgroundCheckpoint();
            ExecutorToolExecution execution;
            try
            {
                execution = BackgroundTool is null
                    ? await _toolGateway.ExecuteAsync(call, _storageSettings!, _sessionFileManifest, _languageCode, _sessionLog!, token)
                    : await BackgroundTool(call, token);
            }
            catch (OperationCanceledException)
            {
                // Read-only tools have no externally visible effect to duplicate.
                if (IsReadOnlyBackgroundTool(call.Function.Name))
                { _inFlightToolCallId = ""; PersistBackgroundCheckpoint(); }
                throw;
            }
            var receipt = _evidenceService.CreateReceipt(call, execution, _actionGraph, _sessionFileManifest, _storageSettings!);
            _evidenceReceipts.Add(receipt);
            _actionGraphService.Reconcile(_actionGraph, _evidenceReceipts);
            _sessionLog!.Write("executor_tool_receipt", receipt);
            if (execution.Success)
            {
                _successfulToolSequence++;
                _successfulToolCalls.Add(call.Function.Name);
                var target = TryReadToolTargetId(call.Function.Arguments);
                if (call.Function.Name == _pendingRequiredTool
                    && (string.IsNullOrEmpty(_pendingRequiredTarget) || target == _pendingRequiredTarget))
                    _pendingRequiredToolSatisfied = true;
                if (!string.IsNullOrWhiteSpace(target)) _successfulToolCalls.Add(CreateToolEvidenceKey(call.Function.Name, target));
            }
            _messages.Add(new() { Role = "tool", ToolCallId = call.Id, Name = call.Function.Name,
                Content = ToolMessageFormatter.WrapToolResult(call.Function.Name, execution.Command, execution.Content) });
            _pendingToolCalls.Remove(call); _inFlightToolCallId = "";
            // No cancellation check between the completed tool and its durable receipt.
            PersistBackgroundCheckpoint();
        }
    }
}
