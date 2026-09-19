using AIHub.Models;

namespace AIHub.Services;

/// <summary>The request boundary used by the studio's sequential role handoff.</summary>
public interface ILiteraryStudioRequests
{
    bool IsBusy { get; }
    int ContextCapacity { get; }
    Task<ParagraphReply> StudioAsync(StudioRequest request, Func<string,string> localize,
        Action<ParagraphReceipt> receipt, Action<int> budget, IProgress<ModelStreamChunk> progress,
        CancellationToken cancellation, Action? attemptStarting = null, Action? preparationStarted = null);
}
