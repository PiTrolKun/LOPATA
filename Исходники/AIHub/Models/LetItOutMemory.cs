namespace AIHub.Models;

public sealed record LetItOutMemoryPair(int Number, int UserOrdinal, int AnswerOrdinal,
    string User, string Answer, DateTimeOffset? UserUtc, DateTimeOffset? AnswerUtc,
    string Fingerprint, string? Heading, bool Attempted, IReadOnlyList<LetItOutAttachment>? Attachments = null);
public sealed record LetItOutMemorySnapshot(int ThroughPair, string Short, string Expanded);
public sealed record LetItOutMemoryRun(string Id, string ConversationId, int FirstPair, int LastPair);
public sealed record LetItOutMemoryCloseRequest(string ConversationId, string RunId, string ModelPath, string Language);

public interface ILetItOutMemoryRuntime
{
    bool HasMemory { get; }
    Task SummarizePairAsync(LetItOutChatRequest request, CancellationToken token);
    Task CloseMemoryAsync(LetItOutMemoryCloseRequest request, BackgroundOperationState? restored, CancellationToken token);
}
