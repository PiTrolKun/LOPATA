using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>
/// Conservative second opinion for an interior substitution. Flattened synonym lists do not select
/// a dictionary sense. Morphology, quotation protection and meter remain the caller's responsibility.
/// </summary>
public static class MusicRhymeInteriorMeaning
{
    public static bool Allows(string original, string candidate, string lineContext,
        Func<string, IReadOnlyList<RagLexiconHit>> lookup)
    {
        var before = Entries(lookup(original)); var after = Entries(lookup(candidate));
        if (before.Length == 0 || after.Length == 0) return false;
        // A form such as вожусь belongs to different verbs. A translation of one branch does not
        // establish that the generated surface form has the intended reading in this sentence.
        var candidateLemmas = after.Select(h => Normalize(h.Word)).Distinct().ToArray();
        if (candidateLemmas.Length != 1) return false;
        var meanings = after.Where(h => h.Kind == "meaning").SelectMany(Senses).ToArray();
        if (meanings.Length > 0 && meanings.All(Restricted)) return false;
        var sourceTranslations = Translations(before); var candidateTranslations = Translations(after);
        // Compare whole translation alternatives: sense != common sense, live != lively.
        if (sourceTranslations.Overlaps(candidateTranslations)) return true;
        var sourceLemmas = before.Select(h => Normalize(h.Word)).ToHashSet(StringComparer.Ordinal);
        var diminutive = meanings.Where(s => !Restricted(s)).SelectMany(s => Strings(s, "glosses"))
            .Select(g => Regex.Match(g, @"^\s*уменьш(?:\.-ласк)?\.\s+(?:к|от)\s+([\p{L}\p{M}]+)", RegexOptions.IgnoreCase))
            .Any(m => m.Success && sourceLemmas.Contains(Normalize(m.Groups[1].Value)));
        // An explicit diminutive relation plus a separately recorded translation permits a modest
        // image change, e.g. snow -> light snow. A gloss alone is insufficient evidence.
        return diminutive && sourceTranslations.Any(a => candidateTranslations.Any(b =>
            b.StartsWith(a + " ", StringComparison.Ordinal) || b.EndsWith(" " + a, StringComparison.Ordinal)));
    }

    private static RagLexiconHit[] Entries(IReadOnlyList<RagLexiconHit> hits) => hits
        .Where(h => h.Kind is "meaning" or "forms")
        .Where(h => !h.Word.Any(char.IsLetter) || !char.IsUpper(h.Word.First(char.IsLetter)))
        .Where(h => !Senses(h).Any(s => Strings(s, "tags").Contains("form-of")))
        .ToArray();

    private static HashSet<string> Translations(IEnumerable<RagLexiconHit> entries) => entries
        .SelectMany(h => Strings(h.Data, "translations"))
        .SelectMany(t => Regex.Split(t, @"[,;]"))
        .Select(t => Regex.Replace(Normalize(t).Trim(), @"\s+", " "))
        .Where(t => t.Length > 1).ToHashSet(StringComparer.Ordinal);

    private static bool Restricted(JsonElement sense) => Strings(sense, "tags").Any(t => t is
        "archaic" or "obsolete" or "dialectal" or "nonstandard" or "rare" or "regional" or "misspelling" or "obscure")
        || Strings(sense, "glosses").Any(g => Regex.IsMatch(g,
            @"^\s*(?:(?:высок|книжн|разг|перен)\.,?\s*)*(?:устар|истор|диал|рег|обл|редк|простореч)\.", RegexOptions.IgnoreCase));

    private static string Normalize(string value) => RagLexiconReader.Normalize(value);
    private static IEnumerable<JsonElement> Senses(RagLexiconHit hit) => Array(hit.Data, "senses");
    private static IEnumerable<JsonElement> Array(JsonElement data, string key) => data.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static IEnumerable<string> Strings(JsonElement data, string key) => Array(data, key)
        .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
}
