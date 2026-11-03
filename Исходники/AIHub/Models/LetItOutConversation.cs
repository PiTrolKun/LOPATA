namespace AIHub.Models;

public sealed record LetItOutSavedMessage(string Role, string Content, bool Complete = true,
    DateTimeOffset? RecordedUtc = null, IReadOnlyList<string>? Attachments = null);

public sealed record LetItOutConversationSummary(string Id, string Title, bool ManualTitle,
    bool Favorite, bool AutoTitleAttempted, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public sealed record LetItOutConversation(LetItOutConversationSummary Summary,
    IReadOnlyList<LetItOutSavedMessage> Messages, string Draft, IReadOnlyList<string>? DraftAttachments = null);

public sealed record LetItOutTitleRequest(string SessionId, string ModelPath,
    IReadOnlyList<LetItOutChatMessage> Messages, string Language);
