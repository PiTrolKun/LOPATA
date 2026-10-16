using AIHub.Models;

namespace AIHub.Services;

/// <summary>Conservative evidence shared by editorial ranking; these are opportunities, not a meter verdict.</summary>
internal sealed class MusicRhymeSelectionContext
{
    internal sealed record LengthSignal(int Count, int Reference, int Support);
    private static readonly int[][][] Schemes = [[[0, 1], [2, 3]], [[0, 2], [1, 3]], [[0, 3], [1, 2]]];
    public MusicRhymeAnalysis Analysis { get; }
    public Dictionary<int, MusicRhymeLine> ByNumber { get; }
    public MusicRhymeLine[][] Groups { get; }
    public MusicRhymeLine[] Eligible { get; }
    public int MaxFragmentLines { get; }
    private readonly int strongKeyUnits;
    private readonly int alternationMinimum;
    private readonly int alternationTolerance;
    public Dictionary<int, LengthSignal> Lengths { get; } = [];
    private readonly Dictionary<int, MusicRhymeLine[]> normalGroups;
    private readonly Dictionary<int, int> normalPositions = [];
    private readonly Dictionary<(int Stanza, int Block), int[][]?> anchors = [];
    private readonly int scopeStart;
    private readonly int scopeLength;

    public MusicRhymeSelectionContext(MusicRhymeAnalysis analysis, int start, int length, CancellationToken token, MusicRhymeTuning tuning)
    {
        Analysis = analysis; scopeStart = start; scopeLength = length;
        MaxFragmentLines = tuning.Integer("analysis.max_fragment_lines");
        strongKeyUnits = tuning.Integer("analysis.strong_key_units");
        alternationMinimum = tuning.Integer("analysis.alternation_min_lines");
        alternationTolerance = tuning.Integer("analysis.alternation_tolerance");
        ByNumber = analysis.Lines.ToDictionary(l => l.Number);
        Groups = analysis.Lines.GroupBy(l => l.Stanza).Select(g => g.ToArray()).ToArray();
        normalGroups = Groups.ToDictionary(g => g[0].Stanza, g => g.Where(l => !l.Performance).ToArray());
        foreach (var group in normalGroups.Values)
            for (var index = 0; index < group.Length; index++) normalPositions[group[index].Number] = index;
        Eligible = analysis.Lines.Where(InScope).ToArray();
        foreach (var group in Groups)
        {
            token.ThrowIfCancellationRequested();
            var measured = group.Where(l => l.Syllables is not null && !l.Performance).ToArray();
            if (measured.Length == 0 || Alternating(measured)) continue;
            var common = measured.GroupBy(l => l.Syllables!.Value).OrderByDescending(g => g.Count()).First();
            if (common.Count() < tuning.Integer("analysis.reference_lines")
                || common.Count() / (double)measured.Length < tuning.Number("analysis.reference_share")) continue;
            foreach (var line in measured.Where(l => Math.Abs(l.Syllables!.Value - common.Key) >= tuning.Integer("analysis.length_difference")))
                Lengths[line.Number] = new(line.Syllables!.Value, common.Key, common.Count());
        }
    }

    public bool InScope(MusicRhymeLine line) => line.Start >= scopeStart && line.Start - scopeStart <= scopeLength - line.Length;
    public static bool Known(MusicRhymeLine line) => line.Readings.Length > 0 && !line.Readings.Any(r => r.Uncertain);
    public static bool Pair(MusicRhymeLine a, MusicRhymeLine b) => a.Language == b.Language && Known(a) && Known(b)
        && a.Readings.Any(r => b.Readings.Any(s => r.Key == s.Key));
    public static bool DifferentEnding(MusicRhymeLine a, MusicRhymeLine b) => MusicRhymeText.Clean(a.Ending) != MusicRhymeText.Clean(b.Ending);

    private bool StrongPair(MusicRhymeLine a, MusicRhymeLine b) => Pair(a, b) && DifferentEnding(a, b)
        && a.Readings.Where(r => b.Readings.Any(s => r.Key == s.Key))
            .Any(r => (a.Language == "en" ? r.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length : r.Key.Length)
                >= strongKeyUnits);

    public bool Anchored(MusicRhymeIssue issue)
    {
        if (issue.Partner is not { } partner || !ByNumber.TryGetValue(issue.Line, out var line) || !ByNumber.ContainsKey(partner)) return false;
        var group = normalGroups[line.Stanza];
        if (!normalPositions.TryGetValue(issue.Line, out var position)) return false;
        var block = group.Skip(position / 4 * 4).Take(4).ToArray();
        if (block.Length != 4 || !block.Any(l => l.Number == partner)) return false;
        var key = (line.Stanza, position / 4);
        if (!anchors.TryGetValue(key, out var scheme))
        {
            var scores = Schemes.Select(s => (Scheme: s, Matches: s.Count(p => StrongPair(block[p[0]], block[p[1]]))))
                .OrderByDescending(s => s.Matches).ToArray();
            scheme = scores[0].Matches > 0 && scores[0].Matches != scores[1].Matches ? scores[0].Scheme : null;
            anchors[key] = scheme;
        }
        return scheme is not null
            && scheme.Any(p => block[p[0]].Number == issue.Line && block[p[1]].Number == partner
                || block[p[1]].Number == issue.Line && block[p[0]].Number == partner);
    }

    public static HashSet<int> CompletedScheme(MusicRhymeLine[] group)
    {
        var completed = new HashSet<int>();
        foreach (var block in group.Chunk(4))
        {
            if (block.Length == 2 && Pair(block[0], block[1])) completed.UnionWith(block.Select(l => l.Number));
            if (block.Length == 4 && Schemes.Any(s => s.All(p => Pair(block[p[0]], block[p[1]]))))
                completed.UnionWith(block.Select(l => l.Number));
        }
        return completed;
    }

    public static MusicRhymeLine[] MotifPrefix(MusicRhymeLine[] lines)
    {
        var text = lines.Select(l => MusicRhymeText.Clean(l.Text)).ToArray();
        for (var period = 1; period <= lines.Length / 2; period++)
            if (lines.Length % period == 0 && Enumerable.Range(0, lines.Length).All(i => text[i] == text[i % period]))
                return lines[..period];
        return lines;
    }

    public static bool BoundedSentence(MusicRhymeLine[] group, int start, int end) =>
        (start == 0 || MusicRhymeText.Terminal(group[start - 1].Text))
        && (end == group.Length - 1 || MusicRhymeText.Terminal(group[end].Text));

    public static bool CutsObservedPair(MusicRhymeLine[] group, IReadOnlyCollection<MusicRhymeLine> nodes)
    {
        var selected = nodes.Select(l => l.Number).ToHashSet();
        // Only inspect crossing edges. Repeated identical words never establish a protected rhyme.
        foreach (var a in nodes)
            foreach (var b in group)
                if (!selected.Contains(b.Number) && DifferentEnding(a, b) && Pair(a, b)) return true;
        return false;
    }

    public int[][] PreservedPairs(CancellationToken token)
    {
        var result = new List<int[]>();
        foreach (var group in Groups)
        {
            if (group.Length > 256)
            {
                // A large rhyme class is a complete multipartite graph: equal ending words are
                // not edges. Store its connected spanning tree, not all O(n²) pairs. Preserving
                // the shared phonetic key on these edges preserves the entire original class.
                // Two anchors are sufficient even when the first ending occurs many times.
                var families = group.Where(Known).SelectMany(line => line.Readings.Select(r => (Line: line, r.Key)))
                    .GroupBy(item => (item.Line.Language, item.Key));
                var seen = new HashSet<(int, int)>();
                foreach (var family in families)
                {
                    token.ThrowIfCancellationRequested();
                    var lines = family.Select(item => item.Line).DistinctBy(line => line.Number).ToArray();
                    var first = lines[0]; var second = lines.FirstOrDefault(line => DifferentEnding(first, line));
                    if (second is null) continue;
                    foreach (var line in lines.Skip(1))
                    {
                        token.ThrowIfCancellationRequested();
                        var anchor = DifferentEnding(first, line) ? first : second;
                        var edge = (Math.Min(anchor.Number, line.Number), Math.Max(anchor.Number, line.Number));
                        if (seen.Add(edge)) result.Add([edge.Item1, edge.Item2]);
                    }
                }
                continue;
            }
            for (var i = 0; i < group.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                for (var j = i + 1; j < group.Length; j++)
                {
                    if ((j & 255) == 0) token.ThrowIfCancellationRequested();
                    if (DifferentEnding(group[i], group[j]) && Pair(group[i], group[j])) result.Add([group[i].Number, group[j].Number]);
                }
            }
        }
        return result.ToArray();
    }

    private bool Alternating(MusicRhymeLine[] lines) => lines.Length >= alternationMinimum
        && Enumerable.Range(0, 2).All(parity =>
    {
        var counts = lines.Where((_, index) => index % 2 == parity).Select(l => l.Syllables!.Value).ToArray();
        return counts.Length >= 2 && counts.Max() - counts.Min() <= alternationTolerance;
    });
}
