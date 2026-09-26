using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed record LiteraryMemorySearchRequest(string Directory, string Fingerprint, string QueryContext,
    ParagraphEvidence Evidence);
public sealed record LiteraryMemorySearchProgress(string Stage, int Completed, int Total, int Resumed,
    string Directory);
public sealed class LiteraryMemorySearchLimitException(string message) : IOException(message);
public sealed class LiteraryMemorySearchMinimumBudgetException(IReadOnlyList<ImageAnalysisHiddenMessage> messages)
    : IOException("The minimum memory reading request does not fit the model context.")
{
    public IReadOnlyList<ImageAnalysisHiddenMessage> Messages { get; } = messages;
}

internal sealed record MemorySearchSource(string Id, string Kind, string Revision, string Text,
    string Coordinate, int Offset, string Label, string[] Materials);
internal sealed record MemorySearchCursor(int Source, int Offset);
internal sealed record MemorySearchWindow(string Source, string Kind, string Label, string Coordinate, int Start,
    int CoveredStart, string Text);
internal sealed record MemorySearchFinding(string Text, string Quote, string Source, int Start,
    int End, string[] Objects);
internal sealed record MemoryReviewItem(string Id, MemorySearchFinding Finding, string Original);
internal sealed record MemoryReviewResult(string Id, MemorySearchFinding[] Findings);
internal sealed record MemorySearchPass(int Version, int Sequence, MemorySearchCursor Start,
    MemorySearchCursor End, MemorySearchWindow[] Windows, MemorySearchFinding[] Findings);
internal sealed record MemorySearchNode(string Id, string Text, string[] Sources, string[] Objects,
    string[] Children);
internal sealed record MemorySearchManifest(int Version, string Key, string Fingerprint,
    string QueryContext, MemorySearchSource[] Sources, ParagraphReceipt[] Receipts);

/// <summary>Delegates operate inside the caller's inference queue. No runtime or UI is owned here.</summary>
public sealed class LiteraryMemorySearch(
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<bool>> fits,
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<string>> execute)
{
    public Task<ParagraphEvidence> RunAsync(LiteraryMemorySearchRequest request,
        Func<ParagraphEvidence, IReadOnlyList<ImageAnalysisHiddenMessage>> buildFinal,
        Action<LiteraryMemorySearchProgress>? progress, CancellationToken token)
        => new LiteraryMemorySearchEngine(fits, execute).RunAsync(request, buildFinal, progress, token);
}
