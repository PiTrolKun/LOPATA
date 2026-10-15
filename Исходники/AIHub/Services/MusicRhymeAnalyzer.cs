using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public static partial class MusicRhymeAnalyzer
{
    [GeneratedRegex(@"\+?[\p{L}\p{M}]+(?:[+'’\-][\p{L}\p{M}]+)*")]
    private static partial Regex Words();
    public static (int Start, int Length) Scope(MusicTextSelection selection, bool selectedOnly)
    {
        if (string.IsNullOrWhiteSpace(selection.Text)) throw new System.IO.InvalidDataException("Music.Rhyme.Empty");
        if (selection.Start < 0 || selection.Length < 0 || selection.Start > selection.Text.Length - selection.Length)
            throw new System.IO.InvalidDataException("Music.Rhyme.InvalidInput");
        if (!selectedOnly) return (0, selection.Text.Length);
        if (selection.Length <= 0) throw new System.IO.InvalidDataException("Music.Rhyme.NoSelection");
        var start = selection.Start; var end = Math.Min(selection.Text.Length, start + selection.Length);
        while (start > 0 && selection.Text[start - 1] is not ('\r' or '\n')) start--;
        if (end > start && selection.Text[end - 1] == '\n') end--;
        if (end > start && selection.Text[end - 1] == '\r') end--;
        while (end < selection.Text.Length && selection.Text[end] is not ('\r' or '\n')) end++;
        return (start, end - start);
    }
    public static MusicRhymeAnalysis Analyze(string text, Func<string, IReadOnlyList<RagLexiconHit>> lookup,
        CancellationToken token = default)
    {
        var lines = new List<MusicRhymeLine>(); var issues = new List<MusicRhymeIssue>();
        var stanza = 0; var number = 0; var performance = false;
        foreach (Match match in Regex.Matches(text, @"(?:\A|(?<=\n)|(?<=\r)(?!\n))[^\r\n]*"))
        {
            token.ThrowIfCancellationRequested();
            var section = Regex.Match(match.Value, @"^\s*\[([^\]]+)\]");
            if (section.Success)
                performance = Regex.IsMatch(section.Groups[1].Value,
                    @"(?i)\b(intro|outro|instrumental|solo|break|вступление|интро|аутро|соло|проигрыш)\b");
            if (RoleMarker(match.Value)) { stanza++; continue; }
            var clean = Regex.Replace(match.Value, @"\[[^\]]*\]|\([^()]*\)", "");
            if (string.IsNullOrWhiteSpace(clean)) { stanza++; continue; }
            var words = Words().Matches(match.Value).Cast<Match>()
                .Where(w => !InsideTag(match.Value, w.Index) && !InsideDirection(match.Value, w.Index)).ToArray();
            if (words.Length == 0) { stanza++; continue; }
            var ending = words[^1]; var ru = MusicRhymePhonetics.IsRussian(ending.Value);
            var readings = MusicRhymePhonetics.Readings(ending.Value, lookup(ending.Value));
            int? syllables = 0;
            foreach (var word in words)
            {
                if (MusicRhymePhonetics.IsRussian(word.Value)) syllables += MusicRhymePhonetics.RussianSyllables(word.Value);
                else
                {
                    var counts = MusicRhymePhonetics.Readings(word.Value, lookup(word.Value)).Select(r => r.Syllables).Distinct().ToArray();
                    if (counts.Length != 1) { syllables = null; break; }
                    syllables += counts[0];
                }
            }
            lines.Add(new(++number, match.Index, match.Length, stanza, match.Value, ending.Value,
                match.Index + ending.Index, ru ? "ru" : "en", syllables, readings, performance));
            if (!performance && (readings.Length == 0 || readings.Any(r => r.Uncertain))) issues.Add(new(number, "Uncertain"));
        }
        foreach (var group in lines.Where(l => !l.Performance).GroupBy(l => l.Stanza))
        {
            // Infer locally from an observed matching pair; never force one scheme onto the whole song.
            foreach (var block in group.Chunk(4))
            {
                if (block.Length == 2) { Compare(block[0], block[1]); continue; }
                if (block.Length != 4)
                {
                    if (block.Length > 2) Review(block);
                    continue;
                }
                int[][] schemes = [[0, 1, 2, 3], [0, 2, 1, 3], [0, 3, 1, 2]];
                var scored = schemes.Select(s => (Scheme: s, Matches: Pair(block[s[0]], block[s[1]]) + Pair(block[s[2]], block[s[3]])))
                    .OrderByDescending(p => p.Matches).ToArray();
                if (scored[0].Matches == 0 || scored[0].Matches == scored[1].Matches)
                {
                    // Unknown scheme is a task for the editor, not proof of correct rhyme or a forced AABB.
                    if (scored[0].Matches < 2) Review(block);
                    continue;
                }
                var s = scored[0].Scheme; Compare(block[s[0]], block[s[1]]); Compare(block[s[2]], block[s[3]]);
            }
            var measured = group.Where(l => l.Syllables is not null).ToArray();
            var common = measured.GroupBy(l => l.Syllables).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (common?.Count() >= 3)
                foreach (var line in measured.Where(l => Math.Abs(l.Syllables!.Value - common.Key!.Value) >= 3))
                    issues.Add(new(line.Number, "Rhythm"));
        }
        return new(lines.ToArray(), issues.Distinct().ToArray());

        void Review(IEnumerable<MusicRhymeLine> block)
        { foreach (var line in block) issues.Add(new(line.Number, "Review")); }
        static int Pair(MusicRhymeLine a, MusicRhymeLine b) => a.Language == b.Language
            && !a.Readings.Any(r => r.Uncertain) && !b.Readings.Any(r => r.Uncertain)
            && a.Readings.Any(r => b.Readings.Any(s => s.Key == r.Key)) ? 1 : 0;
        void Compare(MusicRhymeLine a, MusicRhymeLine b)
        {
            if (a.Language != b.Language || a.Readings.Length == 0 || b.Readings.Length == 0 || Pair(a, b) == 1) return;
            if (a.Readings.Any(r => r.Uncertain) || b.Readings.Any(r => r.Uncertain)) return;
            issues.Add(new(a.Number, "Rhyme", b.Number)); issues.Add(new(b.Number, "Rhyme", a.Number));
        }
    }
    private static bool InsideTag(string text, int position) => text.LastIndexOf('[', position) > text.LastIndexOf(']', position);
    private static bool InsideDirection(string text, int position) => text.LastIndexOf('(', position) > text.LastIndexOf(')', position);
    internal static bool RoleMarker(string text) => Regex.IsMatch(text, @"^\s*(?:\{[^{}\r\n]*\}|\([^()\r\n]*\))\s*$");
}
