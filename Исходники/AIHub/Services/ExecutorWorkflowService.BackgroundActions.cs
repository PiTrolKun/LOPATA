using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ExecutorWorkflowService
{
    public Task<ExecutorTurnResult> ExecuteAsync(
        ExecutorModelArtifact artifact,
        ExecutorHandoffPackage handoff,
        SessionFileManifest sessionFileManifest,
        StorageSettings storageSettings,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ExecuteAsync", new { artifact, handoff, sessionFileManifest, storageSettings },
            attempt => ExecuteAsyncCore(artifact, handoff, sessionFileManifest, storageSettings, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueAsync(
        string userResponse,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueAsync", new { userResponse },
            attempt => ContinueAsyncCore(userResponse, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueAndRunAsync(
        string userResponse,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueAndRunAsync", new { userResponse },
            attempt => ContinueAndRunAsyncCore(userResponse, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueApprovedActionAndRunAsync(
        ExecutorTurnOption approvedOption,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueApprovedActionAndRunAsync", new { approvedOption },
            attempt => ContinueApprovedActionAndRunAsyncCore(approvedOption, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> UpdateFileManifestAsync(
        SessionFileManifest fileManifest,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("UpdateFileManifestAsync", new { fileManifest },
            attempt => UpdateFileManifestAsyncCore(fileManifest, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ConfirmBriefAndRunAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ConfirmBriefAndRunAsync", new { Operation = "result" },
            attempt => ConfirmBriefAndRunAsyncCore(streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorResultSnapshot> CreateResultSnapshotAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("CreateResultSnapshotAsync", new { Operation = "result" },
            attempt => CreateResultSnapshotAsyncCore(streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        string capability,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueAfterCapabilityRequestAsync", new { capability, resultCode, details },
            attempt => ContinueAfterCapabilityRequestAsyncCore(capability, resultCode, details, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueAfterCapabilityRequestAsync", new { capabilities, resultCode, details },
            attempt => ContinueAfterCapabilityRequestAsyncCore(capabilities, resultCode, details, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorTurnResult> ContinueAfterCapabilityRequestAsync(
        IReadOnlyCollection<ExecutorCapabilityRequest> capabilities,
        IReadOnlyCollection<CapabilityAdapterBinding> bindings,
        string resultCode,
        string details,
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("ContinueAfterCapabilityRequestAsync", new { capabilities, bindings, resultCode, details },
            attempt => ContinueAfterCapabilityRequestAsyncCore(capabilities, bindings, resultCode, details, streamProgress, attempt),
            streamProgress, cancellationToken);

    public Task<ExecutorResultSnapshot> CreateFinalResultAsync(
        IProgress<ModelStreamChunk> streamProgress,
        CancellationToken cancellationToken)
        => RunBackgroundAsync("CreateFinalResultAsync", new { Operation = "result" },
            attempt => CreateFinalResultAsyncCore(streamProgress, attempt),
            streamProgress, cancellationToken);
}
