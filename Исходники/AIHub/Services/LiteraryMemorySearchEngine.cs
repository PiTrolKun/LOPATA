using System.IO;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

internal sealed class LiteraryMemorySearchEngine(
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<bool>> fits,
    Func<IReadOnlyList<ImageAnalysisHiddenMessage>, CancellationToken, Task<string>> execute)
{
    private const int InitialCharacters = 16384;
    private const int Attempts = 4; // Initial request and three retries; errors never become empty findings.

    public async Task<ParagraphEvidence> RunAsync(LiteraryMemorySearchRequest request,
        Func<ParagraphEvidence, IReadOnlyList<ImageAnalysisHiddenMessage>> buildFinal,
        Action<LiteraryMemorySearchProgress>? progress, CancellationToken token)
    {
        if (!request.Evidence.Complete) throw new IOException("Mandatory source reading failed.");
        var baseline = new ParagraphEvidence([], request.Evidence.Receipts.Select(r => r with { Materials = [] }).ToArray());
        var mandatory = buildFinal(baseline);
        if (!await fits(mandatory, token).ConfigureAwait(false)) throw new LiteraryMemorySearchMinimumBudgetException(mandatory);
        var sources = await Task.Run(() => LiteraryMemorySearchSources.Build(request.Evidence), token).ConfigureAwait(false);
        using var store = await Task.Run(() => new LiteraryMemorySearchStore(request, sources), token).ConfigureAwait(false);
        var passes = (await Task.Run(() => store.LoadPasses(sources), token).ConfigureAwait(false)).ToList();
        var resumed = passes.Count;
        var cursor = passes.LastOrDefault()?.End ?? new MemorySearchCursor(0, 0);
        var total = checked(sources.Sum(s => s.Text.Length));
        progress?.Invoke(new(resumed > 0 ? "resume" : "reading", Completed(sources, cursor), total, resumed, store.Directory));
        while (cursor.Source < sources.Length)
        {
            token.ThrowIfCancellationRequested();
            var characters = InitialCharacters;
            var failures = 0;
            MemorySearchPass pass;
            while (true)
            {
                var (windows, end) = LiteraryMemorySearchSources.Slice(sources, cursor, characters);
                if (windows.Length == 0) { cursor = end; break; }
                var messages = LiteraryMemorySearchPrompts.Extract(request.QueryContext, windows);
                if (!await fits(messages, token).ConfigureAwait(false))
                {
                    if (characters == 1) throw new LiteraryMemorySearchMinimumBudgetException(messages);
                    characters = Math.Max(1, characters / 2); continue;
                }
                string? raw = null;
                try
                {
                    raw = await execute(messages, token).ConfigureAwait(false);
                    var extracted = LiteraryMemorySearchPrompts.ParseExtraction(raw, windows);
                    pass = new(1, passes.Count + 1, cursor, end, windows, extracted);
                    LiteraryMemorySearchSources.Validate(pass, sources, cursor);
                    // A fully generated and validated pass is committed even if cancellation arrives now.
                    await Task.Run(() => store.Save(pass), CancellationToken.None).ConfigureAwait(false);
                    passes.Add(pass); cursor = end;
                    progress?.Invoke(new("reading", Completed(sources, cursor), total, resumed, store.Directory));
                    break;
                }
                catch (ImageAnalysisContextExhaustedException ex)
                {
                    if (++failures < Attempts && characters > 1)
                    { characters = Math.Max(1, characters / 2); continue; }
                    await Task.Run(() => store.SaveFailure("reading", passes.Count + 1, failures, ex), CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex) when (Retryable(ex))
                {
                    if (++failures < Attempts)
                    {
                        characters = Math.Max(1, characters / 2);
                        continue;
                    }
                    await Task.Run(() => store.SaveFailure("reading", passes.Count + 1, failures, ex), CancellationToken.None).ConfigureAwait(false);
                    throw new LiteraryMemorySearchFailedException("reading", failures, ex);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        // Verify meaning after all reading is complete, against original passages, before the final answer.
        // Draft passes remain independently saved, including those made by earlier versions.
        var findings = await new LiteraryMemorySearchReview(fits, execute).RunAsync(request.QueryContext,
            passes.ToArray(), sources, store, progress, resumed, token).ConfigureAwait(false);
        var nodes = findings.Select((f, index) => new MemorySearchNode("v" + (index + 1),
            f.Text + " [" + f.Source + ":" + f.Start + ".." + f.End + "]", [f.Source], f.Objects, [])).ToArray();
        var lowerBound = Evidence(request.Evidence, sources, [], findings, store.Key, passes.Count);
        if (!await fits(buildFinal(lowerBound), token).ConfigureAwait(false))
            throw new LiteraryMemorySearchLimitException("The retained object registry and source coverage exceed the final context. Findings are saved; narrow the request or increase context.");
        for (var round = 0; round < 24; round++)
        {
            var result = Evidence(request.Evidence, sources, nodes, findings, store.Key, passes.Count);
            if (await fits(buildFinal(result), token).ConfigureAwait(false))
            {
                await Task.Run(() => store.Complete(passes.Count, findings.Length), token).ConfigureAwait(false);
                progress?.Invoke(new("complete", total, total, resumed, store.Directory));
                return result;
            }
            nodes = await ReduceAsync(request.QueryContext, nodes, store, progress, resumed, token).ConfigureAwait(false);
        }
        throw new LiteraryMemorySearchLimitException("Memory summaries could not reach a safe final size. All original findings remain saved.");
    }

    private async Task<MemorySearchNode[]> ReduceAsync(string context, MemorySearchNode[] nodes,
        LiteraryMemorySearchStore store, Action<LiteraryMemorySearchProgress>? progress, int resumed, CancellationToken token)
    {
        var result = new List<MemorySearchNode>(); var before = nodes.Sum(n => n.Text.Length);
        for (var index = 0; index < nodes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = 1;
            var messages = LiteraryMemorySearchPrompts.Merge(context, [nodes[index]]);
            if (!await fits(messages, token).ConfigureAwait(false))
                throw new LiteraryMemorySearchMinimumBudgetException(messages);
            while (count < 16 && index + count < nodes.Length)
            {
                var expanded = LiteraryMemorySearchPrompts.Merge(context, nodes.Skip(index).Take(count + 1).ToArray());
                if (!await fits(expanded, token).ConfigureAwait(false)) break;
                messages = expanded; count++;
            }
            var group = nodes.Skip(index).Take(count).ToArray();
            if (group.Sum(n => n.Text.Length) <= 16) { result.AddRange(group); index += count; continue; }
            var merged = await MergeAsync(context, nodes, index, count, store, token).ConfigureAwait(false);
            result.Add(merged.Node); index += merged.Count;
            progress?.Invoke(new("merge", index, nodes.Length, resumed, store.Directory));
        }
        if (result.Sum(n => n.Text.Length) >= before)
            throw new LiteraryMemorySearchLimitException("Memory reduction cannot get smaller without discarding evidence. Original findings remain saved.");
        return result.ToArray();
    }

    private async Task<(MemorySearchNode Node, int Count)> MergeAsync(string context, MemorySearchNode[] nodes,
        int index, int count, LiteraryMemorySearchStore store, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            MemorySearchNode[] group; IReadOnlyList<ImageAnalysisHiddenMessage> messages;
            while (true)
            {
                group = nodes.Skip(index).Take(count).ToArray();
                messages = LiteraryMemorySearchPrompts.Merge(context, group);
                if (await fits(messages, token).ConfigureAwait(false)) break;
                if (count == 1) throw new LiteraryMemorySearchMinimumBudgetException(messages);
                count = Math.Max(1, count / 2);
            }
            var id = LiteraryMemorySearchSources.Hash(ParagraphJson.Encode(new { version = 1, context, group }));
            var cached = await Task.Run(() => store.LoadMerge(id), token).ConfigureAwait(false);
            if (cached is not null)
            {
                if (cached.Id != id || string.IsNullOrWhiteSpace(cached.Text) || cached.Text.Length >= group.Sum(n => n.Text.Length)
                    || !cached.Children.SequenceEqual(group.Select(n => n.Id))
                    || !cached.Sources.SequenceEqual(group.SelectMany(n => n.Sources).Distinct())
                    || !cached.Objects.SequenceEqual(group.SelectMany(n => n.Objects).Distinct(StringComparer.Ordinal)))
                    throw new InvalidDataException("Invalid saved memory summary. Original files retained.");
                return (cached, count);
            }
            try
            {
                var result = LiteraryMemorySearchPrompts.ParseMerge(await execute(messages, token).ConfigureAwait(false), id, group);
                await Task.Run(() => store.SaveMerge(result), CancellationToken.None).ConfigureAwait(false);
                return (result, count);
            }
            catch (ImageAnalysisContextExhaustedException ex)
            {
                if (attempt < Attempts && count > 1) { count = Math.Max(1, count / 2); continue; }
                await Task.Run(() => store.SaveFailure("merge", index + 1, attempt, ex), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (Retryable(ex))
            {
                if (attempt < Attempts) continue;
                await Task.Run(() => store.SaveFailure("merge", index + 1, attempt, ex), CancellationToken.None).ConfigureAwait(false);
                throw new LiteraryMemorySearchFailedException("merge", attempt, ex);
            }
        }
    }

    private static bool Retryable(Exception ex) => ex is IOException or InvalidDataException or JsonException
        or HttpRequestException or InvalidOperationException or LiteraryLoopException;

    private static int Completed(MemorySearchSource[] sources, MemorySearchCursor cursor) =>
        checked(sources.Take(cursor.Source).Sum(s => s.Text.Length) + cursor.Offset);

    private static ParagraphEvidence Evidence(ParagraphEvidence original, MemorySearchSource[] sources,
        MemorySearchNode[] nodes, MemorySearchFinding[] findings, string key, int passes)
    {
        var relevantSources = findings.Select(f => f.Source).ToHashSet();
        var sampled = original.Receipts.Any(r => r.Status == "partial")
            || sources.Any(s => s.Kind is "original_book_fragment" or "completed_project_fragment");
        var data = new
        {
            summaries = nodes.Select(n => new { n.Text, sources = n.Sources }),
            objects = findings.SelectMany(f => f.Objects.Select(name => new { name, source = f.Source, start = f.Start, end = f.End }))
                .Distinct().ToArray(),
            sources = sources.Where(s => relevantSources.Contains(s.Id)).Select(s => new { s.Id, s.Kind, s.Label, s.Coordinate }),
            coverage = new { readSources = sources.Length, passes, findings = findings.Length,
                completeSelectedMaterialRanges = true, rankedOrPartialSelection = sampled,
                note = sampled
                    ? "All supplied ranges were processed; RAG is a ranked selection or some source ranges were excluded. Do not claim the entire library was read or absence proved."
                    : "All selected material ranges were processed. Findings are model extractions, not a guarantee of semantic completeness. Do not infer contents of unselected sources.",
                semanticReview = "The model reread the referenced original passages and revised these notes for meaning. This is not a guarantee of correctness.",
                counting = "Object entries are model-written names linked to source ranges. Repeated mentions, aliases and events require explicit deduplication; registry length is NOT a verified count.",
                checkpoint = "Dialogs/MemorySearch/" + key }
        };
        var material = new ParagraphMaterial("memory-search/" + key[..12] + "@" + LiteraryMemorySearchSources.Hash(ParagraphJson.Encode(data)),
            "memory_search_findings", key, data);
        var read = sources.SelectMany(s => s.Materials).ToHashSet();
        var receipts = original.Receipts.Select(r => r with
        {
            Materials = r.Materials.Any(read.Contains) ? [material.Id] : [],
            Detail = r.Detail + " All captured ranges were processed in bounded passes; original findings and quotes are saved locally."
        }).ToArray();
        return new([material], receipts);
    }
}
