using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal sealed class LiteraryMemorySearchReview(
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<bool>> fits,
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<string>> execute)
{
    public async Task<MemorySearchFinding[]> RunAsync(string context, MemorySearchPass[] passes,
        MemorySearchSource[] sources, LiteraryMemorySearchStore store, Action<LiteraryMemorySearchProgress>? progress,
        int resumed, CancellationToken token)
    {
        var items = passes.SelectMany(p => p.Findings.Select((f, i) =>
        {
            var source = sources.Single(s => s.Id == f.Source);
            var start = Math.Max(0, f.Start - source.Offset - 256);
            var end = Math.Min(source.Text.Length, f.End - source.Offset + 256);
            if (start > 0 && char.IsLowSurrogate(source.Text[start])) start--;
            if (end < source.Text.Length && end > 0 && char.IsHighSurrogate(source.Text[end - 1])) end++;
            return new MemoryReviewItem("p" + p.Sequence + "f" + i, f, source.Text[start..end]);
        })).ToArray();
        var result = new List<MemorySearchFinding>();
        for (var index = 0; index < items.Length;)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke(new("verify", index, items.Length, resumed, store.Directory));
            var count = 1;
            var messages = LiteraryMemorySearchPrompts.Verify(context, items[index..(index + count)]);
            if (!await fits(messages, token).ConfigureAwait(false)) throw new LiteraryMemorySearchMinimumBudgetException(messages);
            // A small bounded group keeps room for every corrected finding in the response.
            while (count < 6 && index + count < items.Length)
            {
                var expanded = LiteraryMemorySearchPrompts.Verify(context, items[index..(index + count + 1)]);
                if (!await fits(expanded, token).ConfigureAwait(false)) break;
                messages = expanded; count++;
            }
            var group = items[index..(index + count)];
            var id = LiteraryMemorySearchSources.Hash(ParagraphJson.Encode(new { version = 1, context, group }));
            var cached = await Task.Run(() => store.LoadReview(id), token).ConfigureAwait(false);
            if (cached is not null)
            {
                Validate(cached, id, group);
                result.AddRange(cached.Findings); index += count; continue;
            }
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!await fits(messages, token).ConfigureAwait(false)) throw new LiteraryMemorySearchMinimumBudgetException(messages);
                    var findings = LiteraryMemorySearchPrompts.ParseVerification(await execute(messages, token).ConfigureAwait(false), group);
                    var reviewed = new MemoryReviewResult(id, findings);
                    Validate(reviewed, id, group);
                    await Task.Run(() => store.SaveReview(reviewed), CancellationToken.None).ConfigureAwait(false);
                    result.AddRange(findings); break;
                }
                catch (LiteraryMemorySearchMinimumBudgetException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or HttpRequestException
                    or InvalidOperationException or LiteraryLoopException or ImageAnalysisContextExhaustedException)
                {
                    if (attempt < 4) continue;
                    await Task.Run(() => store.SaveFailure("verify", index + 1, attempt, ex), CancellationToken.None).ConfigureAwait(false);
                    if (ex is ImageAnalysisContextExhaustedException) throw;
                    throw new LiteraryMemorySearchFailedException("verify", attempt, ex);
                }
            }
            index += count;
        }
        progress?.Invoke(new("verify", items.Length, items.Length, resumed, store.Directory));
        return LiteraryMemorySearchPrompts.DistinctFindings(result);
    }

    // Validate persisted references and types, never the meaning or literal spelling of model notes.
    private static void Validate(MemoryReviewResult review, string id, MemoryReviewItem[] items)
    {
        if (review.Id != id || review.Findings is null || review.Findings.Any(f => f is null
            || string.IsNullOrWhiteSpace(f.Text) || f.Objects is null || f.Objects.Any(n => n is null)
            || !items.Any(i => i.Finding.Source == f.Source && i.Finding.Start == f.Start
                && i.Finding.End == f.End && i.Finding.Quote == f.Quote)))
            throw new InvalidDataException("Invalid saved memory verification references.");
    }
}
