using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Exact dictionary synonyms and their attested forms, never guessed word endings.</summary>
public static class MusicRhymeVocabulary
{
    public static IEnumerable<string> Related(string word, Func<string, IReadOnlyList<RagLexiconHit>> lookup,
        CancellationToken token = default, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MusicRhymePhonetics.Clean(word) };
        var roots = lookup(word).Where(h => h.Kind == "meaning" && h.Data.TryGetProperty("synonyms", out _))
            .SelectMany(h => h.Data.GetProperty("synonyms").EnumerateArray())
            .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            .Where(Single).Distinct(StringComparer.OrdinalIgnoreCase).Take(tuning.Integer("search.synonym_roots"));
        var groups = new List<string[]>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var forms = lookup(root).Where(h => h.Kind is "meaning" or "forms")
                .Where(h => h.Data.TryGetProperty("forms", out var f) && f.ValueKind == JsonValueKind.Array)
                .SelectMany(h => h.Data.GetProperty("forms").EnumerateArray())
                .Where(f => f.TryGetProperty("form", out var v) && v.ValueKind == JsonValueKind.String)
                .Select(f => f.GetProperty("form").GetString()!).Prepend(root).Where(Single).Distinct().Take(tuning.Integer("search.forms_per_root")).ToArray();
            groups.Add(forms);
        }
        // Do not let a verb's first long conjugation table crowd out all later synonym families.
        for (var at = 0; at < tuning.Integer("search.forms_per_root"); at++)
        foreach (var forms in groups)
        {
            token.ThrowIfCancellationRequested();
            if (at < forms.Length)
            {
                var clean = MusicRhymePhonetics.Clean(forms[at]);
                if (seen.Add(clean)) yield return clean;
                if (seen.Count > tuning.Integer("search.lexical_forms")) yield break;
            }
        }
    }

    public static MusicRhymeSuggestion[] EndingCandidates(MusicRhymeLine line,
        Func<string, IReadOnlyList<RagLexiconHit>> lookup, CancellationToken token = default, MusicRhymeTuning? tuning = null) => Related(line.Ending, lookup, token, tuning)
        .Select(word => new MusicRhymeSuggestion(line.Number, word,
            string.Join(" / ", MusicRhymePhonetics.Readings(word, lookup(word)).Select(r => r.Display)), "lexical-forms", false)).ToArray();

    private static bool Single(string word) => Regex.IsMatch(word, @"^[\p{L}\p{M}]+$");
}
