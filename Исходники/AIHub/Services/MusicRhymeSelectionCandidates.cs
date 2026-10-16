using System.Globalization;
using AIHub.Models;

namespace AIHub.Services;

internal static class MusicRhymeSelectionCandidates
{
    public static IEnumerable<MusicRhymeFragment> Create(MusicRhymeSelectionContext context, CancellationToken token, bool includeVariations = true)
    {
        var any = false;
        var seen = new HashSet<(int, int)>();
        foreach (var issue in context.Analysis.Issues.Where(i => i.Kind == "Rhyme" && context.Anchored(i)))
        {
            token.ThrowIfCancellationRequested();
            var ids = (Math.Min(issue.Line, issue.Partner!.Value), Math.Max(issue.Line, issue.Partner.Value));
            if (!seen.Add(ids)) continue;
            var a = context.ByNumber[ids.Item1]; var b = context.ByNumber[ids.Item2];
            if (Make([a, b], "mismatch", 4 + Math.Abs((a.Syllables ?? 0) - (b.Syllables ?? 0)) / 100.0,
                "Mismatch", a.Ending, b.Ending) is { } mismatch) yield return mismatch;
        }
        foreach (var (number, signal) in context.Lengths)
        {
            token.ThrowIfCancellationRequested();
            var a = context.ByNumber[number];
            var neighbor = context.Groups.Single(g => g[0].Stanza == a.Stanza)
                .Where(l => l.Number != number && l.Syllables == signal.Reference)
                .OrderBy(l => Math.Abs(l.Number - number)).FirstOrDefault();
            if (neighbor is not null)
                if (Make([a, neighbor], "length-opportunity", 2 + Math.Abs(signal.Count - signal.Reference) / 100.0,
                    "Length", Number(signal.Count), Number(signal.Reference), Number(signal.Support)) is { } length) yield return length;
        }
        foreach (var group in context.Groups)
        {
            token.ThrowIfCancellationRequested();
            var normal = MusicRhymeSelectionContext.MotifPrefix(group.Where(l => !l.Performance).ToArray());
            var completed = MusicRhymeSelectionContext.CompletedScheme(normal);
            for (var position = 0; position + 1 < normal.Length; position++)
            {
                var a = normal[position]; var b = normal[position + 1];
                if (!MusicRhymeSelectionContext.BoundedSentence(normal, position, position + 1)
                    || MusicRhymeSelectionContext.CutsObservedPair(normal, [a, b])) continue;
                if (completed.Contains(a.Number) && completed.Contains(b.Number)) continue;
                if (MusicRhymeText.Clean(a.Text) == MusicRhymeText.Clean(b.Text)) continue;
                if (MusicRhymeSelectionContext.Pair(a, b))
                {
                    if (!MusicRhymeSelectionContext.DifferentEnding(a, b))
                        if (Make([a, b], "opportunity", .5, "SameEnding", a.Ending) is { } repeated) yield return repeated;
                    continue;
                }
                if (MusicRhymeSelectionContext.Known(a) && MusicRhymeSelectionContext.Known(b))
                {
                    var difference = Math.Abs((a.Syllables ?? 0) - (b.Syllables ?? 0));
                    if (Make([a, b], "opportunity", 1 + Math.Min(difference, 10) / 100.0, "PairOpportunity", a.Ending, b.Ending) is { } pair)
                        yield return pair;
                }
                else
                {
                    var unknown = new[] { a, b }.Where(l => !MusicRhymeSelectionContext.Known(l)).Select(l => l.Ending);
                    if (Make([a, b], "uncertainty", .25, "Uncertainty", a.Ending, b.Ending, string.Join(", ", unknown)) is { } uncertain)
                        yield return uncertain;
                }
            }
            for (var start = 0; start < normal.Length; start++)
            {
                token.ThrowIfCancellationRequested();
                foreach (var width in new[] { 3, 4 })
                {
                    if (width > context.MaxFragmentLines) continue;
                    var block = normal.Skip(start).Take(width).ToArray();
                    if (block.Length != width || !MusicRhymeSelectionContext.BoundedSentence(normal, start, start + width - 1)
                        || MusicRhymeSelectionContext.CutsObservedPair(normal, block)) continue;
                    if (block.All(l => completed.Contains(l.Number)) || !block.All(context.InScope)) continue;
                    if (block.Select(l => MusicRhymeText.Clean(l.Ending)).Distinct().Count() <= 1) continue;
                    if (Make(block, "opportunity", .8 + Math.Min(block.Sum(l => MusicRhymeText.WordCount(l.Text)), 60) / 10000.0,
                        "CoherentOpportunity", string.Join(" — ", block.Select(l => l.Ending))) is { } coherent) yield return coherent;
                }
            }
        }
        // Every primary score is >= .25; every variation is < .1 and is a fallback family.
        // Therefore none of the quadratic variation family can affect a choice once a primary exists.
        if (!includeVariations || any) yield break;
        foreach (var group in context.Groups)
        {
            var eligible = group.Where(context.InScope).ToArray();
            for (var i = 0; i < eligible.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var a = eligible[i];
                for (var j = i + 1; j < eligible.Length; j++)
                {
                    if ((j & 255) == 0) token.ThrowIfCancellationRequested();
                    var b = eligible[j];
                    if (!MusicRhymeSelectionContext.Pair(a, b) || !MusicRhymeSelectionContext.DifferentEnding(a, b)) continue;
                    var size = MusicRhymeText.WordCount(a.Text) + MusicRhymeText.WordCount(b.Text);
                    if (Make([a, b], "variation", .01 + Math.Min(size, 60) / 10000.0, "RhymingVariation", Number(size), a.Ending, b.Ending) is { } rhyming)
                        yield return rhyming;
                }
            }
            for (var i = 0; i + 1 < eligible.Length; i++)
            {
                var a = eligible[i]; var b = eligible[i + 1];
                if (MusicRhymeText.Clean(a.Text) == MusicRhymeText.Clean(b.Text)) continue;
                var size = MusicRhymeText.WordCount(a.Text) + MusicRhymeText.WordCount(b.Text);
                if (Make([a, b], "variation", Math.Min(size, 60) / 10000.0, "PairVariation", a.Ending, b.Ending) is { } variation)
                    yield return variation;
            }
        }
        if (!any && context.Eligible.Length > 0)
        {
            var a = context.Eligible.OrderByDescending(l => MusicRhymeText.WordCount(l.Text)).ThenBy(l => l.Start).First();
            if (Make([a], "variation", 0, "SingleVariation", a.Ending) is { } single) yield return single;
        }

        MusicRhymeFragment? Make(MusicRhymeLine[] nodes, string kind, double score, string reason, params string[] arguments)
        {
            var selected = nodes.Where(context.InScope).Select(l => l.Number).Distinct().Order().ToArray();
            if (selected.Length == 0 || selected.Length > context.MaxFragmentLines) return null;
            any = true;
            return new(selected, kind, Math.Round(score, 4), "Music.Rhyme.Selection." + reason, arguments)
            { Context = nodes.Select(l => l.Number).Distinct().Order().ToArray() };
        }
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
