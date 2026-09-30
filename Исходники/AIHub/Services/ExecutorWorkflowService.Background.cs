using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ExecutorBackgroundInput(string SessionId, string Action, JsonElement Arguments,
    StorageSettings Storage, ExecutorSessionCheckpoint? Initial, SessionFileManifest Files);
public sealed record ExecutorBackgroundCheckpoint(ExecutorSessionCheckpoint Session, SessionFileManifest Files,
    JsonElement? Result = null);

public sealed partial class ExecutorWorkflowService
{
    public const string BackgroundKind = "sandbox.executor";
    public string BackgroundSessionId { get; set; } = "";
    private StorageSettings? _backgroundStorage;
    private bool _backgroundWorkActive;
    internal Func<IReadOnlyList<StructuredChatMessage>, CancellationToken, Task<StructuredChatResult>>? BackgroundGeneration { get; set; }
    internal Func<StructuredToolCall, CancellationToken, Task<ExecutorToolExecution>>? BackgroundTool { get; set; }

    private async Task<T> RunBackgroundAsync<T>(string action, object arguments,
        Func<CancellationToken, Task<T>> initial, IProgress<ModelStreamChunk> stream, CancellationToken token,
        BackgroundOperationState? restored = null)
    {
        if (ApplicationBackgroundOperations.Current is not { } controller || controller.IsInOperationScope)
            return await initial(token);
        var args = JsonSerializer.SerializeToElement(arguments);
        var storage = _backgroundStorage ?? (args.TryGetProperty("storageSettings", out var stored)
            ? stored.Deserialize<StorageSettings>() : null) ?? throw new InvalidOperationException("Missing executor storage.");
        var input = restored?.Input.Deserialize<ExecutorBackgroundInput>()
            ?? new(BackgroundSessionId, action, args, storage, CreateCheckpoint(), _session?.BackgroundFileManifest ?? new());
        var latest = restored?.Checkpoint?.Deserialize<ExecutorBackgroundCheckpoint>();
        var started = latest is not null;
        _backgroundStorage = input.Storage;
        _backgroundWorkActive = true;
        try
        {
            var title = (_session?.CreateCheckpoint().Handoff.LanguageCode ?? "ru").StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Sandbox" : "Песочница";
            return await ApplicationBackgroundOperations.RunAsync(BackgroundKind, title, input.SessionId, input, async attempt =>
            {
                if (latest?.Result is { } ready) return ready.Deserialize<T>()!;
                T result;
                if (!started)
                {
                    started = true;
                    if (typeof(T) == typeof(ExecutorResultSnapshot)) _session!.BeginBackgroundSnapshot();
                    result = await initial(attempt);
                }
                else if (typeof(T) == typeof(ExecutorResultSnapshot))
                {
                    var snapshot = action == "CreateFinalResultAsync"
                        ? await _session!.CreateFinalResultAsync(stream, attempt)
                        : await _session!.CreateResultSnapshotAsync(stream, attempt);
                    result = (T)(object)snapshot;
                }
                else
                {
                    var turn = await _session!.ResumeBackgroundTurnAsync(stream, attempt);
                    turn = await ResolveRequestedToolsAsync(_session, turn, stream, attempt);
                    result = (T)(object)turn;
                }
                latest = new(_session!.CreateCheckpoint(), _session.BackgroundFileManifest, JsonSerializer.SerializeToElement(result));
                controller.SaveCheckpoint(latest);
                CheckpointChanged?.Invoke(this, EventArgs.Empty);
                return result;
            }, token, restored);
        }
        finally { _backgroundWorkActive = false; }
    }

    private void SaveExecutorBackgroundCheckpoint()
    {
        if (!_backgroundWorkActive || ApplicationBackgroundOperations.Current is not { IsInOperationScope: true } controller) return;
        controller.SaveCheckpoint(new ExecutorBackgroundCheckpoint(_session!.CreateCheckpoint(), _session.BackgroundFileManifest));
    }

    public async Task<object> ResumeBackgroundAsync(BackgroundOperationState state,
        IProgress<ModelStreamChunk> stream, CancellationToken token)
    {
        var input = state.Input.Deserialize<ExecutorBackgroundInput>() ?? throw new InvalidDataException("Missing executor input.");
        var latest = state.Checkpoint?.Deserialize<ExecutorBackgroundCheckpoint>();
        var checkpoint = latest?.Session ?? input.Initial;
        _backgroundStorage = input.Storage; BackgroundSessionId = input.SessionId;
        if (checkpoint is not null)
            Restore(checkpoint, checkpoint.Artifact, latest?.Files ?? input.Files, input.Storage,
                new() { SessionId = input.SessionId, RunId = Guid.NewGuid().ToString("N"), RestoredAt = DateTimeOffset.Now });
        if (input.Action is "CreateFinalResultAsync" or "CreateResultSnapshotAsync")
            return await RunBackgroundAsync(input.Action, input.Arguments,
                attempt => input.Action == "CreateFinalResultAsync" ? CreateFinalResultAsyncCore(stream, attempt)
                    : CreateResultSnapshotAsyncCore(stream, attempt), stream, token, state);
        return await RunBackgroundAsync(input.Action, input.Arguments,
            attempt => ReplayInitialExecutorActionAsync(input, stream, attempt), stream, token, state);
    }

    private Task<ExecutorTurnResult> ReplayInitialExecutorActionAsync(ExecutorBackgroundInput input,
        IProgress<ModelStreamChunk> stream, CancellationToken token)
    {
        T Read<T>(string key) => input.Arguments.GetProperty(key).Deserialize<T>()!;
        return input.Action switch
        {
            "ExecuteAsync" => ExecuteAsyncCore(Read<ExecutorModelArtifact>("artifact"), Read<ExecutorHandoffPackage>("handoff"),
                Read<SessionFileManifest>("sessionFileManifest"), input.Storage, stream, token),
            "ContinueAsync" => ContinueAsyncCore(Read<string>("userResponse"), stream, token),
            "ContinueAndRunAsync" => ContinueAndRunAsyncCore(Read<string>("userResponse"), stream, token),
            "ContinueApprovedActionAndRunAsync" => ContinueApprovedActionAndRunAsyncCore(Read<ExecutorTurnOption>("approvedOption"), stream, token),
            "UpdateFileManifestAsync" => UpdateFileManifestAsyncCore(Read<SessionFileManifest>("fileManifest"), stream, token),
            "ConfirmBriefAndRunAsync" => ConfirmBriefAndRunAsyncCore(stream, token),
            "ContinueAfterCapabilityRequestAsync" when input.Arguments.TryGetProperty("bindings", out _) =>
                ContinueAfterCapabilityRequestAsyncCore(Read<List<ExecutorCapabilityRequest>>("capabilities"), Read<List<CapabilityAdapterBinding>>("bindings"),
                    Read<string>("resultCode"), Read<string>("details"), stream, token),
            "ContinueAfterCapabilityRequestAsync" when input.Arguments.TryGetProperty("capabilities", out _) =>
                ContinueAfterCapabilityRequestAsyncCore(Read<List<ExecutorCapabilityRequest>>("capabilities"), Read<string>("resultCode"), Read<string>("details"), stream, token),
            "ContinueAfterCapabilityRequestAsync" => ContinueAfterCapabilityRequestAsyncCore(Read<string>("capability"),
                Read<string>("resultCode"), Read<string>("details"), stream, token),
            _ => throw new InvalidDataException("Unknown executor background action.")
        };
    }
}
