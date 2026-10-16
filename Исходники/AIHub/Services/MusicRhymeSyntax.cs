using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Local vetoes for word substitutions whose grammatical attachment is not established.</summary>
public static class MusicRhymeSyntax
{
    private static readonly Regex Words = new(@"[\p{L}\p{M}]+");
    private static readonly Regex Backwards = new(@"[\p{L}\p{M}]+", RegexOptions.RightToLeft);
    private static readonly HashSet<string> Prepositions = new("в во на по к ко у от до из с со за под над перед при без для около возле через сквозь между".Split(' '), StringComparer.Ordinal);

    public static bool CanReplace(MusicRhymeLine line, int wordIndex, int wordLength,
        Func<string, IReadOnlyList<RagLexiconHit>> lookup, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var text = line.Text;
        if (wordIndex < 0 || wordLength <= 0 || wordIndex > text.Length - wordLength) return false;
        var target = Words.Match(text, wordIndex);
        if (!target.Success || target.Index != wordIndex || target.Length != wordLength) return false;
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        int Kind(Match word)
        {
            var key = MusicRhymePhonetics.Clean(word.Value);
            if (!cache.TryGetValue(key, out var kind)) cache[key] = kind = Kinds(key, lookup);
            return kind;
        }
        var category = Kind(target);
        if (category == 0) return true; // No invented grammar when the caller has no dictionary evidence.
        if ((category & (category - 1)) != 0) return false;
        bool Joined(Match a, Match b) => b.Index > a.Index + a.Length
            && text.AsSpan(a.Index + a.Length, b.Index - a.Index - a.Length).Trim().IsEmpty;
        var previous = new List<Match>();
        for (var m = Backwards.Match(text, wordIndex); m.Success && previous.Count < tuning.Integer("program.previous_words"); m = m.NextMatch()) previous.Add(m);
        if (category == 2)
        {
            var next = Words.Match(text, wordIndex + wordLength);
            return previous.Count > 0 && Joined(previous[0], target) && Kind(previous[0]) == 1
                || next.Success && Joined(target, next) && Kind(next) == 1;
        }
        if (category != 1) return true;
        var count = 0; var right = target;
        while (count < previous.Count && count < tuning.Integer("program.previous_adjectives") && Joined(previous[count], right) && (Kind(previous[count]) & 2) != 0)
        { right = previous[count]; count++; }
        return !(count > 0 && count < previous.Count && Joined(previous[count], right)
            && Prepositions.Contains(previous[count].Value.ToLowerInvariant()));
    }

    private static int Kinds(string word, Func<string, IReadOnlyList<RagLexiconHit>> lookup)
    {
        var result = 0;
        foreach (var hit in lookup(word).Where(h => h.Kind is "meaning" or "forms"))
        {
            if (hit.Word.Any(char.IsLetter) && char.IsUpper(hit.Word.First(char.IsLetter))) continue;
            if (!hit.Data.TryGetProperty("forms", out var found) || found.ValueKind != JsonValueKind.Array) continue;
            var forms = found.EnumerateArray().ToArray();
            string[] Tags(JsonElement f) => f.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];
            var exact = forms.Where(f => f.TryGetProperty("form", out var s) && s.ValueKind == JsonValueKind.String
                && MusicRhymePhonetics.Clean(s.GetString()!.Trim('(', ')')) == word).ToArray();
            if (exact.Length == 0) continue;
            var tags = forms.SelectMany(Tags).ToArray(); var own = exact.SelectMany(Tags).ToArray();
            var nounMetadata = hit.Data.TryGetProperty("gender", out var g) && g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString());
            var verb = hit.Data.TryGetProperty("aspect", out _) || tags.Any(t => Regex.IsMatch(t, @"^(presfut_|past_|imperative_|infinitive$|present$|past$|future$)"));
            var adjective = tags.Any(t => t.StartsWith("decl_", StringComparison.Ordinal) || t.StartsWith("short_", StringComparison.Ordinal))
                || !nounMetadata && tags.Any(t => t is "masculine" or "feminine" or "neuter");
            var noun = nounMetadata || own.Any(t => Regex.IsMatch(t, @"^(sg|pl)_(nom|gen|dat|acc|inst|prep)$")
                || t is "nominative" or "genitive" or "dative" or "accusative" or "instrumental" or "prepositional");
            result |= verb ? 4 : adjective ? 2 : noun ? 1 : 0;
        }
        return result;
    }
}
