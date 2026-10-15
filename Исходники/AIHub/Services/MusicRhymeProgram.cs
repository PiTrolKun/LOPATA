using AIHub.Models;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>A draft, never an automatic main-editor edit. Replace only a dictionary synonym, one side per pair.</summary>
public static class MusicRhymeProgram
{
    public static string Build(MusicRhymeStep step, IReadOnlyList<MusicRhymeSuggestion> candidates,
        Func<string, IReadOnlyList<RagLexiconHit>>? lookup = null)
    {
        var changes = new List<(int Start, int Length, string Word)>(); var used = new HashSet<int>();
        foreach (var issue in MusicRhymeWork.Issues(step).Where(i => i.Kind == "Rhyme" && i.Partner is not null))
        {
            if (used.Contains(issue.Line) || used.Contains(issue.Partner!.Value)) continue;
            var line = step.Analysis!.Lines.Single(l => l.Number == issue.Line);
            var partner = step.Analysis.Lines.Single(l => l.Number == issue.Partner);
            if (lookup is not null && line.Language == "ru" && !line.Text.Any(c => c is '+' or '\'' or '’'))
            {
                // A Russian inversion keeps every word and syllable. It is still a draft requiring review.
                var movable = Regex.Matches(line.Text, @"[\p{L}\p{M}]+")
                    .Where(w => line.Start + w.Index < line.EndingStart && w.Index > 0
                        && !line.Text[..w.Index].Contains('[') && MusicRhymePhonetics.RussianSyllables(w.Value) > 1)
                    .FirstOrDefault(w => MusicRhymePhonetics.Readings(w.Value, lookup(w.Value))
                        .Any(r => !r.Uncertain && partner.Readings.Any(p => p.Key == r.Key && !p.Uncertain)));
                if (movable is not null)
                {
                    var prefix = line.Text[..(line.EndingStart + line.Ending.Length - line.Start)];
                    var cut = movable.Length;
                    while (movable.Index + cut < prefix.Length && char.IsWhiteSpace(prefix[movable.Index + cut])) cut++;
                    var reordered = prefix.Remove(movable.Index, cut).TrimEnd() + " " + movable.Value;
                    changes.Add((line.Start - step.Request.Start, prefix.Length, reordered)); used.Add(issue.Line); used.Add(issue.Partner!.Value); continue;
                }
            }
            // Explicit user stress marks belong to the original word, and must never disappear silently.
            if (line.Ending.Any(c => c is '\u0301' or '+' or '\'' or '’')) continue;
            var match = candidates.FirstOrDefault(c => c.Line == issue.Line && c.Synonym);
            if (match is null) continue;
            var word = match.Word;
            if (char.IsUpper(line.Ending[0])) word = char.ToUpperInvariant(word[0]) + word[1..];
            changes.Add((line.EndingStart - step.Request.Start, line.Ending.Length, word)); used.Add(issue.Line); used.Add(issue.Partner.Value);
        }
        var text = step.Request.Text;
        foreach (var edit in changes.OrderByDescending(e => e.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Word);
        return text;
    }
}
