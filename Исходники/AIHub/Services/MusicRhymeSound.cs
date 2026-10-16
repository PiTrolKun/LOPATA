using AIHub.Models;

namespace AIHub.Services;

/// <summary>Similarity of the stressed phonetic tails. This says nothing about the meaning of a word.</summary>
public static class MusicRhymeSound
{
    public const double MinimumNearSimilarity = 0.72;
    private const string RussianVowels = "аэиоуы";
    private static readonly string[] EnglishVowels = ["AA", "AE", "AH", "AO", "AW", "AY", "EH", "ER", "EY", "IH", "IY", "OW", "OY", "UH", "UW"];
    private static readonly string[][] RussianNeighbours = [["б", "п"], ["в", "ф"], ["г", "к", "х"], ["д", "т"], ["ж", "ш", "щ", "ч"], ["з", "с", "ц"], ["м", "н"], ["л", "р"]];
    private static readonly string[][] EnglishNeighbours = [["B", "P"], ["D", "T"], ["G", "K"], ["V", "F"], ["Z", "S"], ["ZH", "SH", "CH", "JH"], ["DH", "TH"], ["M", "N", "NG"], ["L", "R"]];

    public static double Similarity(MusicRhymeReading? a, MusicRhymeReading? b, MusicRhymeTuning? tuning = null)
    {
        if (a is null || b is null || a.Language != b.Language || a.Language is not ("ru" or "en")) return 0;
        tuning ??= new();
        var left = Phones(a.Key, a.Language, tuning); var right = Phones(b.Key, b.Language, tuning);
        if (left.Length == 0 || right.Length == 0 || !IsVowel(left[0], a.Language) || !IsVowel(right[0], a.Language)) return 0;
        if (left.SequenceEqual(right)) return 1;
        var tail = TailSimilarity(left.Skip(1).ToArray(), right.Skip(1).ToArray(), a.Language);
        // A long identical suffix cannot compensate for a different stressed vowel.
        return left[0] == right[0] ? 0.65 + 0.35 * tail : Math.Min(tuning.Number("quality.stressed_vowel_ceiling"), 0.34 + 0.35 * tail);
    }

    /// <summary>Finite, indexed lookup keys; never a wildcard scan of the dictionary.</summary>
    internal static IEnumerable<string> NeighbourKeys(MusicRhymeReading reading, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var phones = Phones(reading.Key, reading.Language, tuning);
        if (phones.Length == 0 || !IsVowel(phones[0], reading.Language)) yield break;
        var emitted = new HashSet<string>(StringComparer.Ordinal) { reading.Key };
        yield return reading.Key;
        var groups = reading.Language == "ru" ? RussianNeighbours : EnglishNeighbours;
        // Substitute related consonants before less likely deletions; keep the stressed vowel.
        for (var i = 1; i < phones.Length; i++)
            foreach (var group in groups.Where(g => g.Contains(phones[i], StringComparer.Ordinal)))
                foreach (var phone in group)
                {
                    var changed = (string[])phones.Clone(); changed[i] = phone;
                    var key = Key(changed, reading.Language);
                    if (emitted.Count >= tuning.Integer("search.neighbour_keys")) yield break;
                    if (emitted.Add(key) && Similarity(reading, reading with { Key = key }, tuning) >= tuning.Number("quality.near_similarity")) yield return key;
                }
        for (var i = 1; i < phones.Length; i++)
        {
            var key = Key(phones.Where((_, at) => at != i), reading.Language);
            if (emitted.Count >= tuning.Integer("search.neighbour_keys")) yield break;
            if (emitted.Add(key) && Similarity(reading, reading with { Key = key }, tuning) >= tuning.Number("quality.near_similarity")) yield return key;
        }
        // Common terminal clusters are useful slant rhymes. Limit insertion to one terminal phone.
        var endings = reading.Language == "ru" ? new[] { "т", "с", "н", "м", "л", "р", "к" } : new[] { "T", "S", "N", "M", "L", "R", "K" };
        foreach (var phone in endings)
        {
            var key = Key(phones.Append(phone), reading.Language);
            if (emitted.Count >= tuning.Integer("search.neighbour_keys")) yield break;
            if (emitted.Add(key) && Similarity(reading, reading with { Key = key }, tuning) >= tuning.Number("quality.near_similarity")) yield return key;
        }
    }

    private static string[] Phones(string key, string language, MusicRhymeTuning tuning)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > tuning.Integer("quality.phonetic_characters")) return [];
        var result = language == "ru" ? key.Select(c => c.ToString()).ToArray() : key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return result.Length > tuning.Integer("quality.phonetic_units") ? [] : result;
    }
    private static string Key(IEnumerable<string> phones, string language) => string.Join(language == "ru" ? "" : " ", phones);
    private static bool IsVowel(string phone, string language) => language == "ru" ? phone.Length == 1 && RussianVowels.Contains(phone[0]) : EnglishVowels.Contains(phone, StringComparer.Ordinal);
    private static double TailSimilarity(string[] left, string[] right, string language)
    {
        var length = Math.Max(left.Length, right.Length); if (length == 0) return 1;
        var previous = Enumerable.Range(0, right.Length + 1).Select(x => (double)x).ToArray();
        var current = new double[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : Related(left[i - 1], right[j - 1], language) ? 0.4 : 1;
                current[j] = Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, previous[j - 1] + cost));
            }
            (previous, current) = (current, previous);
        }
        return Math.Clamp(1 - previous[^1] / length, 0, 1);
    }
    private static bool Related(string a, string b, string language) =>
        (language == "ru" ? RussianNeighbours : EnglishNeighbours).Any(g => g.Contains(a, StringComparer.Ordinal) && g.Contains(b, StringComparer.Ordinal));
}
