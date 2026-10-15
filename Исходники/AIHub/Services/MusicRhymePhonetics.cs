using System.Text;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Conservative phonetic keys, not a claim to model every sung pronunciation.</summary>
public static class MusicRhymePhonetics
{
    public const string RussianVowels = "аеёиоуыэюя";
    public static string Clean(string word) => Regex.Replace(word.Normalize(NormalizationForm.FormC).ToLowerInvariant(), "[+\u0301\u0300'’]", "");
    public static bool IsRussian(string word) => Regex.IsMatch(word, "[а-яё]", RegexOptions.IgnoreCase);
    public static int RussianSyllables(string text) => text.ToLowerInvariant().Count(c => RussianVowels.Contains(c));
    public static MusicRhymeReading? Russian(string variant, bool uncertain = false)
    {
        var lowered = variant.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var letters = new StringBuilder(); var stress = -1;
        for (var i = 0; i < lowered.Length; i++)
        {
            var c = lowered[i];
            if (c is '\u0301' or '\'' or '’') { if (letters.Length > 0 && RussianVowels.Contains(letters[^1])) stress = letters.Length - 1; continue; }
            if (c == '+') { stress = letters.Length; continue; }
            if (!char.IsLetter(c)) continue;
            if (c == 'ё' && stress < 0) stress = letters.Length;
            letters.Append(c);
        }
        var word = letters.ToString(); var syllables = RussianSyllables(word);
        if (syllables == 1) stress = word.IndexOfAny(RussianVowels.ToCharArray());
        if (syllables == 0 || stress < 0 || stress >= word.Length || !RussianVowels.Contains(word[stress])) return null;
        var tail = word[stress..].Replace("ь", "").Replace("ъ", "").Select(c => c switch { 'ё' => 'о', 'я' => 'а', 'ю' => 'у', 'е' => 'э', _ => c }).ToArray();
        // Final devoicing and the common regressive devoicing before voiceless consonants.
        const string voiced = "бвгджз", voiceless = "пфктшс";
        for (var i = tail.Length - 1; i >= 1; i--)
        {
            var index = voiced.IndexOf(tail[i]);
            if (index >= 0 && (i == tail.Length - 1 || "пфктсшщчхц".Contains(tail[i + 1]))) tail[i] = voiceless[index];
            if (i > 0 && tail[i] == 'о') tail[i] = 'а';
        }
        return new(word, "ru", new string(tail), syllables, variant, uncertain);
    }
    public static MusicRhymeReading? English(string word, IEnumerable<string> phonemes, bool uncertain = false)
    {
        var phones = phonemes.ToArray();
        var stressed = Array.FindLastIndex(phones, p => p.EndsWith('1'));
        if (stressed < 0) stressed = Array.FindLastIndex(phones, p => p.EndsWith('2'));
        if (stressed < 0) stressed = Array.FindLastIndex(phones, p => p.EndsWith('0'));
        if (stressed < 0) return null;
        var syllables = phones.Count(p => p.Length > 0 && char.IsDigit(p[^1]));
        return new(word, "en", string.Join(" ", phones.Skip(stressed).Select(p => Regex.Replace(p, "[012]$", ""))),
            syllables, string.Join(" ", phones), uncertain);
    }
    public static MusicRhymeReading[] Readings(string word, IReadOnlyList<RagLexiconHit> hits)
    {
        var ru = IsRussian(word);
        if (ru && (word.Contains('\u0301') || word.Contains('+') || word.Contains('\'') || word.Contains('’')))
            return Russian(word) is { } explicitReading ? [explicitReading] : [];
        var result = new List<MusicRhymeReading>();
        foreach (var hit in hits.Where(h => ru ? h.Kind is "stress" or "forms" : h.Kind == "pronunciation"))
        {
            if (ru && hit.Data.TryGetProperty("variants", out var variants))
                foreach (var item in variants.EnumerateArray())
                    if (Clean(item.GetString()!) == Clean(word) && Russian(item.GetString()!, hit.RequiresContext) is { } reading) result.Add(reading);
            if (!ru && hit.Data.TryGetProperty("phonemes", out var phones)
                && English(word, phones.EnumerateArray().Select(p => p.GetString()!), hit.RequiresContext) is { } english) result.Add(english);
        }
        if (ru && result.Count == 0 && Russian(word) is { } single) result.Add(single);
        var unique = result.DistinctBy(r => (r.Language, r.Key)).ToArray();
        return unique.Select(r => r with { Uncertain = r.Uncertain || unique.Length > 1 }).ToArray();
    }
}
