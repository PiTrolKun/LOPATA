using AIHub.Models;

namespace AIHub.Services;

internal static class LetItOutContextWindow
{
    internal const int AnswerReserve = LetItOutChatRuntime.AnswerTokens + 512;

    internal static async Task<int> FitAsync(List<LetItOutChatMessage> tail, int capacity,
        Func<IReadOnlyList<LetItOutChatMessage>, Task<int>> count)
    {
        var used = await count(tail);
        while ((long)used + AnswerReserve >= capacity)
        {
            if (tail.Count <= 1) throw new LetItOutChatContextException();
            tail.RemoveAt(0);
            while (tail.Count > 1 && tail[0].Role == "assistant") tail.RemoveAt(0);
            used = await count(tail);
        }
        return used;
    }

    // Binary search whole source boundaries. A large library needs logarithmic tokenization calls.
    internal static async Task<(int Boundary, int Used)> ExtraReserveAsync(int used, int capacity,
        IReadOnlyList<int> boundaries, Func<int, Task<int>> countAfter)
    {
        if (boundaries.Count == 0) return (0, used);
        var target = Math.Max(0, used - (int)Math.Ceiling(capacity * .10));
        var low = 0; var high = boundaries.Count - 1;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (await countAfter(boundaries[middle]) <= target) high = middle; else low = middle + 1;
        }
        return (boundaries[low], await countAfter(boundaries[low]));
    }
}
