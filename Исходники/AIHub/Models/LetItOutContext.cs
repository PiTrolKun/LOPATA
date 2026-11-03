namespace AIHub.Models;

public sealed record LetItOutContextInfo(int UsedTokens, int Capacity, bool CanTrim, bool HasArchived)
{
    public CoreMemoryStatus Status => new() { IsActive = true, UsedUnits = UsedTokens,
        LimitUnits = Capacity, HasCompressedSummary = HasArchived };
}

public interface ILetItOutContextRuntime
{
    bool HasContext { get; }
    Task<LetItOutContextInfo> MeasureContextAsync(LetItOutChatRequest request, CancellationToken token);
    Task<LetItOutContextInfo> TrimContextAsync(LetItOutChatRequest request, CancellationToken token);
}
