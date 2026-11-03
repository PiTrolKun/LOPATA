using System.IO;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LetItOutChatRuntime : ILetItOutContextRuntime
{
    public Action<string, LetItOutContextInfo>? ContextMeasured { get; set; }
    public bool HasContext => MemoryStore is not null;
    private static string ContextReference(string id) => $"\nОглавление: memory://{id}/index.md\n";

    public async Task<LetItOutContextInfo> MeasureContextAsync(LetItOutChatRequest request, CancellationToken token)
        => (await ReadContextViewAsync(request, token)).Info;

    private sealed record ContextView(LetItOutContextInfo Info, int Start, int Cut,
        IReadOnlyList<LetItOutMemoryPair> Pairs, IReadOnlyList<LetItOutSavedMessage> Saved);

    private async Task<ContextView> ReadContextViewAsync(LetItOutChatRequest request, CancellationToken token)
    {
        // Counting an idle resident never starts, warms or changes a model.
        var store = MemoryStore ?? throw new InvalidOperationException("Chat memory is not configured.");
        var saved = store.Load(request.SessionId);
        if (saved is null && (request.Messages.Count != 0 || store.IsDeleted(request.SessionId))) throw new InvalidDataException("Conversation was removed.");
        var sourceMessages = saved?.Messages ?? [];
        var start = saved is null ? 0 : store.ContextStart(request.SessionId, request.Messages);
        var described = await AttachmentInputsAsync(request, false, token);
        var tail = described.Messages.Skip(start).ToList(); var originalCount = tail.Count;
        var capacity = await _runtime.LetItOutEffectiveCapacityAsync(token);
        var used = await LetItOutContextWindow.FitAsync(tail, capacity, messages =>
            _runtime.CountLetItOutPromptAsync(BuildPayload(messages, MemoryGuard + ContextReference(request.SessionId)), token));
        start += originalCount - tail.Count;
        var pairs = store.MemoryPairs(request.SessionId);
        var canTrim = pairs.Take(Math.Max(0, pairs.Count - 1)).Any(p => LetItOutConversationStore.ContextPairEnd(sourceMessages, p) > start);
        return new(new(used, capacity, canTrim, start > 0), start, store.ContextCut(request.SessionId), pairs, sourceMessages);
    }

    public async Task<LetItOutContextInfo> TrimContextAsync(LetItOutChatRequest request, CancellationToken token)
    {
        var view = await ReadContextViewAsync(request, token);
        if (!view.Info.CanTrim) return view.Info;
        var described = await AttachmentInputsAsync(request, false, token);
        var candidates = view.Pairs.Take(view.Pairs.Count - 1)
            .Select(p => (Pair: p.Number, End: LetItOutConversationStore.ContextPairEnd(view.Saved, p)))
            .Where(p => p.End > view.Start).ToArray();
        var selected = await LetItOutContextWindow.ExtraReserveAsync(view.Info.UsedTokens, view.Info.Capacity,
            candidates.Select(p => p.End).ToArray(), end => _runtime.CountLetItOutPromptAsync(
                BuildPayload(described.Messages.Skip(end).ToArray(), MemoryGuard + ContextReference(request.SessionId)), token));
        token.ThrowIfCancellationRequested();
        var through = candidates.Single(p => p.End == selected.Boundary).Pair;
        if (!MemoryStore!.SetContextCut(request.SessionId, view.Cut, through)) throw new InvalidDataException("Context changed during trimming.");
        return new(selected.Used, view.Info.Capacity, through < candidates[^1].Pair, true);
    }
}
