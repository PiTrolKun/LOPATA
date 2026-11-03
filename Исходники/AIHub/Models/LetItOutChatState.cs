using System.IO;

namespace AIHub.Models;

public sealed record LetItOutChatMessage(string Role, string Content, IReadOnlyList<string>? Attachments = null,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string>? ImageInputs = null);

// Keep the serialized CoreModelPath name for old checkpoints. It now identifies the scenario model.
// Conversation history ends with the pending user message; incomplete assistant text is separate.
public sealed record LetItOutChatRequest(string SessionId, string TurnId, string CoreModelPath,
    IReadOnlyList<LetItOutChatMessage> Messages, string Language);

// The recovery store keeps only active work and removes private final states.
// The separate conversation library retains the user's chat history and drafts.
public sealed record LetItOutChatCheckpoint(LetItOutChatRequest Request, string Partial,
    string? Result, bool Completed);

public sealed class LetItOutChatContextException() : InvalidOperationException("LetItOut.ContextFull");
public sealed class LetItOutChatIncompleteException() : IOException("LetItOut.Incomplete");
public sealed class LetItOutModelChangedException() : InvalidOperationException("LetItOut.ModelChanged");
public sealed class LetItOutMemoryException(string key) : IOException(key);
