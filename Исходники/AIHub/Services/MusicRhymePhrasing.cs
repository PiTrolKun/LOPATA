using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed record MusicRhymePhrase(string Text, string Reason);

/// <summary>Small grammatical alternatives, never a random shuffle or a claim of artistic improvement.</summary>
public static class MusicRhymePhrasing
{
    private const string Word = "[А-Яа-яЁё]+";
    private static readonly Regex Possessive = new(@"^(?:(?:мо|тво|сво)(?:й|я|ё|е|ю|и|его|ей|ему|им|ём|ем|их|ими)|(?:наш|ваш)(?:а|е|и|его|ей|ему|им|ем|их|ими|у)?|его|её|ее|их)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Pointers = new("это этот эта эти тот та то те кто что когда где здесь там он она оно они я ты мы вы".Split(' '), StringComparer.Ordinal);

    public static IEnumerable<MusicRhymePhrase> Alternatives(MusicRhymeLine line,
        Func<string, IReadOnlyList<RagLexiconHit>>? lookup, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var text = line.Text;
        if (line.Language != "ru" || line.Performance || text.IndexOfAny(['\u0301', '\u0300', '+', '\'', '’', '‘', '"', '«', '»', '“', '”', '[', ']', '(', ')', '{', '}', ':', '=', '\r', '\n']) >= 0
            || Regex.IsMatch(text, @"^\s*[—–-]\s")) yield break;
        var cache = new Dictionary<string, Form[]>(StringComparer.Ordinal);
        Form[] Forms(string word)
        {
            var key = MusicRhymePhonetics.Clean(word);
            if (!cache.TryGetValue(key, out var value)) cache[key] = value = Read(key, lookup);
            return value;
        }

        // Only reorder a complete prepositional noun group. A following noun could belong to the possessive.
        foreach (Match match in Regex.Matches(text, $@"\b(?<prep>в|во|на|к|ко|у|от|до|из|с|со|за|под|над|перед|при|без|для|около|возле)(?<a>[ \t]+)(?<noun>{Word})(?<b>[ \t]+)(?<poss>{Word})\b", RegexOptions.IgnoreCase))
        {
            var noun = match.Groups["noun"].Value; var possessive = match.Groups["poss"].Value;
            if (!Possessive.IsMatch(possessive) || char.IsUpper(noun[0])) continue;
            var allowed = Cases(match.Groups["prep"].Value.ToLowerInvariant());
            var nouns = Forms(noun).Where(f => f.Category == "noun" && allowed.Contains(f.Case)).ToArray();
            var independent = possessive.ToLowerInvariant() is "его" or "её" or "ее" or "их";
            if (!nouns.Any(n => independent || Forms(possessive).Any(p => Agree(n, p)))) continue;
            var tail = text[(match.Index + match.Length)..].TrimStart();
            if (tail.Length > 0 && char.IsLetter(tail[0]))
            {
                // 'У друга его ждут' may mean 'they await him'; an indeclinable pronoun is not proven possessive here.
                if (independent) continue;
                var next = Regex.Match(tail, "^" + Word).Value;
                if (!Forms(next).Any(f => f.Finite)) continue;
            }
            var replacement = match.Groups["prep"].Value + match.Groups["a"].Value + possessive + match.Groups["b"].Value + noun;
            yield return new(text.Remove(match.Index, match.Length).Insert(match.Index, replacement), "PossessiveOrder");
        }

        // A written nominal equation supplies the reason for compactness; standalone demonstrative 'это' does not.
        var copula = Regex.Match(text, $@"^[ \t]*[*_]{{0,2}}(?<subject>{Word}(?:[ \t]+{Word}){{0,3}})[ \t]+[—–-][ \t]+(?<filler>это[ \t]+)(?<predicate>{Word}.*)$", RegexOptions.IgnoreCase);
        if (copula.Success)
        {
            var subject = Regex.Matches(copula.Groups["subject"].Value, Word).Select(m => m.Value).ToArray();
            var predicate = Regex.Matches(copula.Groups["predicate"].Value, Word).Select(m => m.Value).Take(4).ToArray();
            var first = predicate.FirstOrDefault(w => w.ToLowerInvariant() is not ("не" or "как"));
            if (!subject.Any(w => Pointers.Contains(w.ToLowerInvariant())) && first is not null && !Pointers.Contains(first.ToLowerInvariant())
                && (lookup is null || subject.Any(w => Forms(w).Any(f => f.Category == "noun" && f.Case == "nom"))
                    && predicate.Any(w => Forms(w).Any(f => f.Category == "noun" && f.Case == "nom"))))
            {
                var filler = copula.Groups["filler"];
                yield return new(text.Remove(filler.Index, filler.Length), "CopulaCompactness");
            }
        }

        // A locative 'в/на + prepositional case' is moved as a whole, after a verified simple S-V core.
        // Directional/object complements such as 'верю в чудо' do not satisfy this locative proof.
        if (lookup is null) yield break;
        foreach (var phrase in MusicRhymeClauseOrder.Alternatives(text, Forms, tuning)) yield return phrase;
        var simple = Regex.Match(text, $@"^(?<lead>[ \t]*[*_]{{0,2}})(?<body>{Word}(?:[ \t]+{Word})*)(?<end>[,.!?…]*[*_]{{0,2}}[ \t]*)$");
        if (!simple.Success) yield break;
        var body = simple.Groups["body"].Value;
        var words = Regex.Matches(body, Word).Cast<Match>().ToArray();
        if (words.Length < 4 || words.Skip(1).Any(w => w.Value.ToLowerInvariant() is "и" or "а" or "но" or "или" or "лишь" or "только" or "даже" or "ли" or "бы")) yield break;
        var verbAt = words[1].Value.Equals("не", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        if (verbAt >= words.Length || !SubjectVerb(words[0].Value, Forms(words[0].Value), Forms(words[verbAt].Value))) yield break;
        for (var prepAt = Math.Max(verbAt + 1, words.Length - 3); prepAt < words.Length - 1; prepAt++)
        {
            if (words[prepAt].Value.ToLowerInvariant() is not ("в" or "во" or "на")) continue;
            var noun = Forms(words[^1].Value).Where(f => f.Category == "noun" && f.Case == "prep").ToArray();
            if (noun.Length == 0) continue;
            if (words.Length - prepAt == 3 && !noun.Any(n => Forms(words[^2].Value).Any(a => a.Category == "adjective" && Agree(n, a)))) continue;
            var middle = words.Skip(verbAt + 1).Take(prepAt - verbAt - 1).ToArray();
            if (middle.Length > tuning.Integer("program.scene_middle_words") || middle.Length > 0 && !Forms(middle[^1].Value).Any(f => f.Category == "noun" && (f.Case == "acc" || verbAt == 2 && f.Case == "gen"))) continue;
            if (middle.Length == 2 && !Forms(middle[^1].Value).Any(n => n.Category == "noun"
                && Forms(middle[0].Value).Any(a => a.Category == "adjective" && Agree(n, a)))) continue;
            var core = body[..words[prepAt].Index].TrimEnd();
            if (core.Skip(1).Any(char.IsUpper)) continue; // Preserve names and deliberately capitalized words.
            var setting = body[words[prepAt].Index..];
            var alternative = simple.Groups["lead"].Value + char.ToUpperInvariant(setting[0]) + setting[1..]
                + " " + char.ToLowerInvariant(core[0]) + core[1..] + simple.Groups["end"].Value;
            yield return new(alternative, "SceneFronting");
            yield break;
        }
    }

    internal sealed record Form(string Category, string Case, string Number, string Gender, string Person, bool Finite);
    internal static bool Agree(Form a, Form b) => a.Case.Length > 0 && a.Case == b.Case
        && a.Number.Length > 0 && a.Number == b.Number
        && (a.Number == "pl" || a.Gender.Length == 0 || b.Gender.Length == 0 || a.Gender == b.Gender);
    private static string[] Cases(string prep) => prep switch {
        "в" or "во" or "на" => ["acc", "prep"], "к" or "ко" => ["dat"],
        "у" or "от" or "до" or "из" or "без" or "для" or "около" or "возле" => ["gen"],
        "с" or "со" => ["gen", "inst"], "за" or "под" => ["acc", "inst"],
        "над" or "перед" => ["inst"], "при" => ["prep"], _ => [] };

    internal static bool SubjectVerb(string subject, Form[] nouns, Form[] verbs)
    {
        var pronoun = subject.ToLowerInvariant() switch {
            "я" => ("sg", "", "1"), "ты" => ("sg", "", "2"), "он" => ("sg", "m", "3"),
            "она" => ("sg", "f", "3"), "оно" => ("sg", "n", "3"), "мы" => ("pl", "", "1"),
            "вы" => ("pl", "", "2"), "они" => ("pl", "", "3"), _ => ("", "", "3") };
        var subjects = pronoun.Item1.Length > 0 ? new[] { new Form("noun", "nom", pronoun.Item1, pronoun.Item2, pronoun.Item3, false) }
            : nouns.Where(n => n.Category == "noun" && n.Case == "nom").Select(n => n with { Person = "3" }).ToArray();
        return subjects.Any(n => verbs.Any(v => v.Finite && (v.Number.Length == 0 || v.Number == n.Number)
            && (v.Gender.Length == 0 || n.Gender.Length == 0 || v.Gender == n.Gender)
            && (v.Person.Length == 0 || v.Person == n.Person)));
    }

    private static Form[] Read(string surface, Func<string, IReadOnlyList<RagLexiconHit>>? lookup)
    {
        if (lookup is null) return [];
        var result = new List<Form>();
        foreach (var hit in lookup(surface).Where(h => h.Kind is "forms" or "meaning" && !h.RequiresContext))
        {
            if (hit.Word.Length == 0 || char.IsUpper(hit.Word[0])) continue;
            var data = hit.Data;
            if (!data.TryGetProperty("forms", out var forms) || forms.ValueKind != JsonValueKind.Array) continue;
            foreach (var form in forms.EnumerateArray())
            {
                if (!form.TryGetProperty("form", out var spelling) || spelling.ValueKind != JsonValueKind.String
                    || MusicRhymePhonetics.Clean(spelling.GetString()!.Trim('(', ')')) != surface
                    || !form.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
                var raw = tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray();
                var tokens = raw.SelectMany(t => t.Split('_')).ToHashSet(StringComparer.Ordinal);
                string Tag(params string[] values) => values.FirstOrDefault(tokens.Contains) ?? "";
                var @case = Tag("nom", "gen", "dat", "acc", "inst", "prep", "nominative", "genitive", "dative", "accusative", "instrumental", "prepositional");
                @case = @case switch { "nominative" => "nom", "genitive" => "gen", "dative" => "dat", "accusative" => "acc", "instrumental" => "inst", "prepositional" => "prep", _ => @case };
                var number = Tag("sg", "pl", "singular", "plural");
                number = number switch { "singular" => "sg", "plural" => "pl", _ => number };
                var gender = Tag("m", "f", "n", "masculine", "feminine", "neuter");
                if (gender.Length == 0 && data.TryGetProperty("gender", out var g) && g.ValueKind == JsonValueKind.String) gender = g.GetString()!;
                gender = gender switch { "masculine" => "m", "feminine" => "f", "neuter" => "n", _ => gender };
                var person = Tag("first-person", "second-person", "third-person");
                person = person switch { "first-person" => "1", "second-person" => "2", "third-person" => "3", _ => "" };
                foreach (var token in tokens.Where(t => Regex.IsMatch(t, "^(sg|pl)[123]$"))) { number = token[..2]; person = token[2..]; }
                var finite = tokens.Overlaps(["presfut", "past", "present", "future"]) && !tokens.Overlaps(["participle", "infinitive", "gerund"]);
                if (finite && tokens.Contains("past") && tokens.IsSupersetOf(["masculine", "feminine", "neuter"]))
                { number = "pl"; gender = ""; }
                if (finite && number.Length == 0 && gender.Length > 0) number = "sg";
                if (number.Length == 0 && gender.Length > 0 && tokens.Contains("decl")) number = "sg";
                var category = finite || data.TryGetProperty("aspect", out _) ? "verb"
                    : raw.Any(t => t.StartsWith("decl_", StringComparison.Ordinal) || t.StartsWith("short_", StringComparison.Ordinal))
                        || tokens.Overlaps(["masculine", "feminine", "neuter"]) && !data.TryGetProperty("gender", out _) ? "adjective"
                    : @case.Length > 0 && number.Length > 0 ? "noun" : "";
                result.Add(new(category, @case, number, gender, person, finite));
            }
        }
        return result.ToArray();
    }
}
