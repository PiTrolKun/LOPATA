using AIHub.Models;

namespace AIHub.Services;

/// <summary>Selects a small, explained editorial task without changing or declaring a song defective.</summary>
public static class MusicRhymeSelection
{
    public const int Version = 1;

    public static MusicRhymeAnalysis Select(MusicRhymeAnalysis analysis, int scopeStart, int scopeLength,
        CancellationToken token = default, MusicRhymeTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (scopeStart < 0 || scopeLength < 0 || scopeStart > int.MaxValue - scopeLength)
            throw new System.IO.InvalidDataException("Music.Rhyme.InvalidInput");
        token.ThrowIfCancellationRequested();
        tuning ??= new();
        var context = new MusicRhymeSelectionContext(analysis, scopeStart, scopeLength, token, tuning);
        var budget = Math.Max(tuning.Integer("analysis.minimum_lines"),
            (int)Math.Ceiling(context.Eligible.Length * tuning.Number("analysis.line_share")));
        var variationLimit = tuning.Integer("analysis.variation_fragments");
        var selected = new List<MusicRhymeFragment>(); var used = new HashSet<int>(); var signatures = new HashSet<string>(StringComparer.Ordinal);
        // Exact sort-and-greedy result with bounded candidate storage: one best item per pass.
        // Equal items retain source enumeration order, matching the stable research sort.
        for (var pass = 0; pass < tuning.Integer("analysis.max_fragments"); pass++)
        {
            MusicRhymeFragment? best = null; string? bestSignature = null;
            foreach (var candidate in MusicRhymeSelectionCandidates.Create(context, token, pass < variationLimit))
            {
                token.ThrowIfCancellationRequested();
                if (candidate.Lines.Any(used.Contains)) continue;
                if (used.Count + candidate.Lines.Length > budget
                    && (selected.Count > 0 || !tuning.Enabled("analysis.first_over_budget"))) continue;
                if (candidate.Kind == "variation" && selected.Count(f => f.Kind == "variation") >= variationLimit) continue;
                if (best is not null && Compare(candidate, best) >= 0) continue;
                var signature = string.Join('\n', candidate.Lines.Select(n => MusicRhymeText.Clean(context.ByNumber[n].Text)));
                if (signatures.Contains(signature)) continue;
                best = candidate; bestSignature = signature;
            }
            if (best is null) break;
            selected.Add(best); used.UnionWith(best.Lines); signatures.Add(bestSignature!);
            if (best.Kind == "variation" && selected.Count(f => f.Kind == "variation") >= variationLimit) break;
        }
        return analysis with { SelectionVersion = Version, Fragments = selected.ToArray(), PreserveRhymePairs = context.PreservedPairs(token) };

        int Compare(MusicRhymeFragment a, MusicRhymeFragment b)
        {
            var score = b.Score.CompareTo(a.Score);
            if (score != 0) return score;
            var start = context.ByNumber[a.Lines[0]].Start.CompareTo(context.ByNumber[b.Lines[0]].Start);
            if (start != 0) return start;
            for (var i = 0; i < Math.Min(a.Lines.Length, b.Lines.Length); i++)
            {
                var line = a.Lines[i].CompareTo(b.Lines[i]);
                if (line != 0) return line;
            }
            return a.Lines.Length.CompareTo(b.Lines.Length);
        }
    }
}
