using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Reorder verified clause constituents, keeping all words and the untouched remainder of the line.</summary>
internal static class MusicRhymeClauseOrder
{
    private const string Word = "[А-Яа-яЁё]+";
    private static readonly HashSet<string> Pronouns = new("я ты он она оно мы вы они".Split(' '), StringComparer.Ordinal);

    internal static IEnumerable<MusicRhymePhrase> Alternatives(string text, Func<string, MusicRhymePhrasing.Form[]> forms, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var clause = Regex.Match(text, $@"^(?<lead>[ \t]*[*_]{{0,2}})(?<body>{Word}(?:[ \t]+{Word})*)(?<tail>[ \t]*(?:[,.;!?…].*)?[*_]{{0,2}}[ \t]*)$");
        if (!clause.Success) yield break;
        var body = clause.Groups["body"].Value;
        var words = Regex.Matches(body, Word).Cast<Match>().ToArray();
        if (words.Length < 2) yield break;
        bool Fits(int subject, int verb) => MusicRhymePhrasing.SubjectVerb(words[subject].Value,
            forms(words[subject].Value), forms(words[verb].Value));
        string Render(string value) => clause.Groups["lead"].Value + value + clause.Groups["tail"].Value;
        static string Upper(string value) => char.ToUpperInvariant(value[0]) + value[1..];
        static string Lower(string value) => char.ToLowerInvariant(value[0]) + value[1..];
        var first = words[0].Value.ToLowerInvariant();
        if (Pronouns.Contains(first) && !words[0].Value.Skip(1).Any(char.IsUpper))
        {
            var verb = words[1].Value.Equals("не", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            if (verb < words.Length && Fits(0, verb))
            {
                var end = words[verb].Index + words[verb].Length;
                var predicate = body[words[1].Index..end];
                yield return new(Render(Upper(predicate) + " " + Lower(words[0].Value) + body[end..]), "PoeticOrder");
            }
            // Pronoun + a complete locative + finite predicate. Never split a noun group or change its forms.
            for (var predicate = 3; predicate <= tuning.Integer("program.predicate_position") && predicate < words.Length; predicate++)
            {
                var verbAt = words[predicate].Value.Equals("не", StringComparison.OrdinalIgnoreCase) ? predicate + 1 : predicate;
                if (verbAt >= words.Length || !Fits(0, verbAt)
                    || words[1].Value.ToLowerInvariant() is not ("в" or "во" or "на") || !Group(1, predicate, ["prep"])) continue;
                var setting = body[words[1].Index..words[predicate].Index].TrimEnd();
                yield return new(Render(Upper(setting) + " " + Lower(words[0].Value) + " " + body[words[predicate].Index..]), "SceneFronting");
            }
        }
        // Complete initial prepositional phrase + one nominative noun subject + a finite predicate.
        var lastVerb = words.Length - 1;
        var nounAt = words.Length - (words[^2].Value.Equals("не", StringComparison.OrdinalIgnoreCase) ? 3 : 2);
        if (nounAt < 2 || nounAt > tuning.Integer("program.phrase_group_words") || !forms(words[nounAt].Value).Any(f => f.Category == "noun" && f.Case == "nom")
            || !Fits(nounAt, lastVerb) || !Group(0, nounAt, Cases(first))) yield break;
        var initial = body[..words[nounAt].Index].TrimEnd();
        var core = body[words[nounAt].Index..];
        if (initial.Skip(1).Any(char.IsUpper) || core.Skip(1).Any(char.IsUpper)) yield break;
        yield return new(Render(Upper(core) + " " + Lower(initial)), "SceneReordering");

        bool Group(int start, int end, string[] cases)
        {
            if (cases.Length == 0 || end - start < 2 || end - start > tuning.Integer("program.phrase_group_words")) return false;
            for (var head = start + 1; head < end; head++)
            foreach (var noun in forms(words[head].Value).Where(f => f.Category == "noun" && cases.Contains(f.Case)))
            {
                if (!Enumerable.Range(start + 1, head - start - 1).All(i => forms(words[i].Value)
                    .Any(a => a.Category == "adjective" && MusicRhymePhrasing.Agree(noun, a)))) continue;
                if (head + 1 < end && !forms(words[end - 1].Value).Any(f => f.Category == "noun" && f.Case == "gen")) continue;
                if (!Enumerable.Range(head + 1, end - head - 1).All(i => forms(words[i].Value)
                    .Any(f => f.Category is "noun" or "adjective" && f.Case == "gen"))) continue;
                return true;
            }
            return false;
        }
    }

    private static string[] Cases(string prep) => prep switch {
        "в" or "во" or "на" => ["acc", "prep"], "по" or "к" or "ко" => ["dat"],
        "у" or "от" or "до" or "из" or "без" or "для" or "около" or "возле" => ["gen"],
        "с" or "со" => ["gen", "inst"], "за" or "под" => ["acc", "inst"],
        "над" or "перед" or "между" => ["inst"], "через" or "сквозь" => ["acc"], "при" => ["prep"], _ => [] };
}
