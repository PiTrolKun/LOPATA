using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class LetItOutChatWork(ILetItOutChatRuntime runtime)
{
    public const string Kind = "chat.let_it_out";
    public bool CanGenerateTitle => runtime is ILetItOutChatTitleRuntime;
    public Task<string> GenerateTitleAsync(LetItOutTitleRequest request, CancellationToken token) =>
        runtime is ILetItOutChatTitleRuntime titles ? titles.GenerateTitleAsync(request, token)
            : throw new InvalidOperationException("Title runtime is not configured.");
    public Task RetireAsync() => runtime.RetireAsync();
    public bool HasTree => runtime is ILetItOutTreeRuntime;
    public Task UpdateTreeAsync(LetItOutTreeRequest request, BackgroundOperationState? restored, CancellationToken token) =>
        runtime is ILetItOutTreeRuntime tree ? tree.UpdateTreeAsync(request, restored, token) : Task.CompletedTask;
    public bool HasContext => runtime is ILetItOutContextRuntime { HasContext: true };
    public Task<LetItOutContextInfo> MeasureContextAsync(LetItOutChatRequest request, CancellationToken token) =>
        ((ILetItOutContextRuntime)runtime).MeasureContextAsync(request, token);
    public Task<LetItOutContextInfo> TrimContextAsync(LetItOutChatRequest request, CancellationToken token) =>
        ((ILetItOutContextRuntime)runtime).TrimContextAsync(request, token);
    public bool HasMemory => runtime is ILetItOutMemoryRuntime { HasMemory: true };
    public Task SummarizePairAsync(LetItOutChatRequest request, CancellationToken token) =>
        runtime is ILetItOutMemoryRuntime memory ? memory.SummarizePairAsync(request, token) : Task.CompletedTask;
    public Task CloseMemoryAsync(LetItOutMemoryCloseRequest request, BackgroundOperationState? restored, CancellationToken token) =>
        runtime is ILetItOutMemoryRuntime memory ? memory.CloseMemoryAsync(request, restored, token) : Task.CompletedTask;

    public async Task<string> GenerateAsync(LetItOutChatRequest request, IProgress<string> progress,
        CancellationToken token, BackgroundOperationState? restored = null,
        Func<string, CancellationToken, Task>? afterAnswer = null)
    {
        // Freeze the UI list once; neither streaming nor a resumed attempt appends a user message.
        request = Snapshot(request);
        var checkpoint = new LetItOutChatCheckpoint(request, "", null, false);
        if (restored is not null)
        {
            if (restored.Kind != Kind || restored.Id != request.TurnId
                || !SameRequest(request, restored.Input.Deserialize<LetItOutChatRequest>()))
                throw new InvalidDataException("LetItOut.InvalidCheckpoint");
            if (restored.Checkpoint is { } saved)
            {
                checkpoint = saved.Deserialize<LetItOutChatCheckpoint>()
                    ?? throw new InvalidDataException("LetItOut.InvalidCheckpoint");
                if (!SameRequest(request, checkpoint.Request)
                    || checkpoint.Completed && string.IsNullOrWhiteSpace(checkpoint.Result)
                    || !checkpoint.Completed && checkpoint.Result is not null)
                    throw new InvalidDataException("LetItOut.InvalidCheckpoint");
                checkpoint = checkpoint with { Request = request,
                    Partial = LetItOutChatRuntime.CleanPartial(checkpoint.Partial),
                    Result = checkpoint.Completed ? LetItOutChatRuntime.CleanAnswer(checkpoint.Result!) : null };
            }
        }
        var state = (restored ?? new BackgroundOperationState { Id = request.TurnId, Kind = Kind,
            Title = request.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Let it out" : "Если накипело",
            Project = request.SessionId, Input = JsonSerializer.SerializeToElement(request) }) with {
                Private = true, Input = JsonSerializer.SerializeToElement(request),
                Checkpoint = JsonSerializer.SerializeToElement(checkpoint) };
        return await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, request.SessionId, request, async attempt =>
        {
            attempt.ThrowIfCancellationRequested();
            if (checkpoint.Completed)
            {
                progress.Report(checkpoint.Result!);
                if (afterAnswer is not null) await afterAnswer(checkpoint.Result!, attempt);
                return checkpoint.Result!;
            }
            checkpoint = new(request, "", null, false);
            progress.Report("");
            Save();
            var partial = new StringBuilder();
            var sinceSave = Stopwatch.StartNew();
            try
            {
                var answer = await runtime.GenerateAsync(request, new InlineProgress<ModelStreamChunk>(chunk =>
                {
                    if (chunk.Text.Length == 0) return;
                    partial.Append(chunk.Text);
                    checkpoint = checkpoint with { Partial = partial.ToString() };
                    progress.Report(checkpoint.Partial);
                    if (sinceSave.ElapsedMilliseconds < 500) return;
                    Save(); sinceSave.Restart();
                }), attempt);
                attempt.ThrowIfCancellationRequested();
                answer = LetItOutChatRuntime.CleanAnswer(answer);
                checkpoint = checkpoint with { Partial = "", Result = answer, Completed = true };
                Save();
                progress.Report(answer);
                if (afterAnswer is not null) await afterAnswer(answer, attempt);
                return answer;
            }
            catch (Exception error) when (error is LetItOutChatContextException or LetItOutModelChangedException)
            {
                Save();
                await runtime.RetireAsync();
                // An unchanged conversation cannot fit on retry. Release the pending-job gate.
                throw new BackgroundOperationFailedException(error.Message);
            }
            catch (LetItOutMemoryException error)
            {
                Save();
                await runtime.RetireAsync();
                throw new BackgroundOperationWaitingException(error.Message);
            }
            catch
            {
                // Error/pause/exit keeps the latest visible partial, never an assistant turn.
                Save();
                await runtime.RetireAsync();
                throw;
            }
        }, token, state);

        void Save() => ApplicationBackgroundOperations.Current?.SaveCheckpoint(checkpoint);
    }

    internal static LetItOutChatRequest Snapshot(LetItOutChatRequest request)
    {
        if (!Guid.TryParseExact(request.SessionId, "N", out _) || !Guid.TryParseExact(request.TurnId, "N", out _)
            || string.IsNullOrWhiteSpace(request.CoreModelPath) || string.IsNullOrWhiteSpace(request.Language)
            || request.Messages is null || request.Messages.Count == 0)
            throw new InvalidDataException("LetItOut.InvalidRequest");
        var messages = new List<LetItOutChatMessage>();
        foreach (var message in request.Messages)
        {
            if (message is null || message.Role is not ("user" or "assistant") || message.Content is null)
                throw new InvalidDataException("LetItOut.InvalidRequest");
            if (message.Role == "assistant")
            {
                try { messages.Add(message with { Content = LetItOutChatRuntime.CleanAnswer(message.Content) }); }
                catch (LetItOutChatIncompleteException) { } // A legacy thought-only response is not an assistant answer.
            }
            else
            {
                if (string.IsNullOrWhiteSpace(message.Content) && message.Attachments is not { Count: > 0 }) throw new InvalidDataException("LetItOut.InvalidRequest");
                if (message.Attachments is { } attachments && (attachments.Count > LetItOutConversationStore.AttachmentLimit
                    || attachments.Distinct().Count() != attachments.Count || attachments.Any(id => !Guid.TryParseExact(id, "N", out _))))
                    throw new InvalidDataException("LetItOut.InvalidRequest");
                messages.Add(message with { Attachments = message.Attachments?.ToArray(), ImageInputs = null });
            }
        }
        // A canceled answer leaves its user's words in history. The next user message remains distinct.
        if (request.Messages[0].Role != "user" || request.Messages[^1].Role != "user")
            throw new InvalidDataException("LetItOut.InvalidRequest");
        return request with { Messages = messages.ToArray() };
    }

    private static bool SameRequest(LetItOutChatRequest request, LetItOutChatRequest? saved) => saved is not null
        && request.SessionId == saved.SessionId && request.TurnId == saved.TurnId
        && request.CoreModelPath == saved.CoreModelPath && request.Language == saved.Language
        && saved.Messages is not null && request.Messages.Count == saved.Messages.Count
        && request.Messages.Zip(Snapshot(saved).Messages).All(p => p.First.Role == p.Second.Role && p.First.Content == p.Second.Content
            && (p.First.Attachments ?? []).SequenceEqual(p.Second.Attachments ?? []));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
