using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Lexical evidence, not an assertion that a replacement fits every sense or sentence.</summary>
public sealed record MusicRhymeWordFit(double Meaning, double Grammar, string Relation, bool Compatible)
{
    public bool GrammarKnown { get; init; }
}

/// <summary>Bounded exact dictionary lookups. Sound or spelling similarity never creates semantic evidence.</summary>
public static class MusicRhymeWordEvidence
{
    private static readonly HashSet<string> Stop = new(("это этот того того либо нечто что как для при или его ее они оно она " +
        "кого чего кому чему каким какой либо кого-либо чего-либо т п д же быть иметь значение действие значению " +
        "перен разг книжн устар спец част место вид человек something someone thing being have with from into that this " +
        "which their some used especially other person state make not the and one any more than such also").Split(' '), StringComparer.Ordinal);

    public static MusicRhymeWordFit Evaluate(string original, string candidate, string lineContext,
        Func<string, IReadOnlyList<RagLexiconHit>> lookup, MusicRhymeTuning? tuning = null)
    {
        tuning ??= new();
        var before = Describe(original, lookup(original)); var after = Describe(candidate, lookup(candidate));
        if (after.ProperOnly) return new(0, 0, "proper-name", false) { GrammarKnown = true };
        if (after.RestrictedOnly) return new(0, 0, "restricted-register", false) { GrammarKnown = true };
        var grammar = Grammar(before, after);
        var context = Words(lineContext).Except(before.Lemmas).Except(after.Lemmas).ToHashSet(StringComparer.Ordinal);
        var relation = Meaning(before, after, context, tuning);
        // A synonym's translation can provide a documented bridge, e.g. far -> far off/away.
        // Only exact dictionary translations are compared; no guessed roots or letter similarity.
        if (relation.Score < .55 && before.Hits.Count > 0 && after.Hits.Count > 0)
        {
            foreach (var word in before.Synonyms.Order(StringComparer.Ordinal).Take(tuning.Integer("quality.synonym_bridges")))
            {
                var bridge = Describe(word, lookup(word));
                if (bridge.ProperOnly) continue;
                if (bridge.Translations.Overlaps(after.Translations) || bridge.Synonyms.Overlaps(after.Lemmas))
                { relation = (.55, "synonym-bridge"); break; }
            }
        }
        if (relation.Score == 0)
        {
            // A context association is weaker than a relation to the replaced word and is labelled separately.
            var common = before.Definitions.Intersect(after.Definitions).Where(context.Contains).ToArray();
            if (common.Length > 0) relation = (.45, "context-definition");
        }
        return new(relation.Score, grammar.Score, relation.Kind, grammar.Compatible) { GrammarKnown = grammar.Known };
    }

    private sealed record Lexical(List<RagLexiconHit> Hits, HashSet<string> Lemmas, HashSet<string> Synonyms,
        HashSet<string> Definitions, HashSet<string> Translations, List<Dictionary<string, string>> Forms, bool ProperOnly, bool RestrictedOnly);

    private static Lexical Describe(string surface, IReadOnlyList<RagLexiconHit> hits)
    {
        var all = hits.Where(h => h.Kind is "meaning" or "forms").ToList();
        // Ordinary words can coexist with a surname or place of the same spelling. Keep the ordinary senses.
        var common = all.Where(h => !Proper(h)).ToList();
        var meanings = common.Where(h => h.Kind == "meaning" && Glosses(h.Data).Any()).ToArray();
        var restrictedOnly = meanings.Length > 0 && meanings.All(Restricted);
        var ordinary = common.Where(h => h.Kind != "meaning" || !Restricted(h)).ToList();
        var lemmas = ordinary.Select(h => Normalize(h.Word)).ToHashSet(StringComparer.Ordinal); lemmas.Add(Normalize(surface));
        var synonyms = ordinary.SelectMany(h => Strings(h.Data, "synonyms")).Select(Normalize)
            .Where(SingleWord).ToHashSet(StringComparer.Ordinal);
        var definitions = ordinary.SelectMany(h => Glosses(h.Data)).SelectMany(Words).ToHashSet(StringComparer.Ordinal);
        var translations = ordinary.SelectMany(h => Strings(h.Data, "translations")).SelectMany(Words).ToHashSet(StringComparer.Ordinal);
        var forms = new List<Dictionary<string, string>>();
        foreach (var hit in ordinary)
        {
            var data = hit.Data;
            var lemmaHits = ordinary.Where(h => Normalize(h.Word) == Normalize(hit.Word)).ToArray();
            var categories = lemmaHits.Select(h => Category(h.Data)).Where(c => c.Length > 0).Distinct().ToArray();
            var category = Category(data);
            if (category.Length == 0 && categories.Length == 1) category = categories[0];
            foreach (var form in Array(data, "forms"))
            {
                if (Get(form, "form") is not { } word || Normalize(word.Trim('(', ')')) != Normalize(surface)) continue;
                var tags = Strings(form, "tags").ToArray(); var reading = Tags(tags, category);
                // Sparse Kaikki forms must not bypass gender/animacy known from another record of this lemma.
                foreach (var key in new[] { "gender", "animate" })
                {
                    var values = lemmaHits.Select(h => Get(h.Data, key)).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
                    if (values.Length == 1) reading.TryAdd(key, values[0]!);
                }
                if (tags.Any(t => t.StartsWith("presfut", StringComparison.Ordinal))
                    && lemmaHits.Any(h => Get(h.Data, "aspect") == "perfective")) reading["tense"] = "future";
                if (reading.Count > 0) forms.Add(reading);
            }
            if (Get(data, "synset") is { Length: > 0 } synset)
                forms.Add(new() { ["category"] = synset[^1] switch { 'n' => "noun", 'v' => "verb", 'a' or 's' => "adjective", 'r' => "adverb", _ => "unknown" } });
        }
        return new(ordinary, lemmas, synonyms, definitions, translations, forms, all.Count > 0 && common.Count == 0, restrictedOnly);
    }

    private static (double Score, string Kind) Meaning(Lexical a, Lexical b, HashSet<string> context, MusicRhymeTuning tuning)
    {
        var ids = a.Hits.Select(h => Get(h.Data, "synset")).Where(s => s is not null).ToHashSet();
        if (b.Hits.Any(h => Get(h.Data, "synset") is { } id && ids.Contains(id))) return (1, "synset");
        var sharedTranslation = a.Translations.Overlaps(b.Translations);
        if (a.Synonyms.Overlaps(b.Lemmas) || b.Synonyms.Overlaps(a.Lemmas))
        {
            // Kaikki's synonym list is flattened across senses; matching morphology does not select a sense
            // or establish a verb's government. Keep uncorroborated polysemy below strong semantic admission.
            var polysemous = a.Hits.Sum(h => Glosses(h.Data).Count()) > tuning.Integer("quality.polysemy_glosses") || b.Hits.Sum(h => Glosses(h.Data).Count()) > tuning.Integer("quality.polysemy_glosses");
            var contextSupport = a.Definitions.Intersect(b.Definitions).Any(context.Contains);
            return polysemous && !sharedTranslation && !contextSupport ? (.7, "polysemous-synonym") : (1, "synonym");
        }
        if (sharedTranslation) return (.6, "shared-translation");
        if (a.Definitions.Overlaps(b.Lemmas) || b.Definitions.Overlaps(a.Lemmas)) return (.7, "meaning-reference");
        if (a.Synonyms.Overlaps(b.Synonyms)) return (.65, "related-synonym");
        if (a.Definitions.Intersect(b.Definitions).Take(tuning.Integer("quality.shared_definition_words")).Count() == tuning.Integer("quality.shared_definition_words")) return (.55, "related-definition");
        return (0, "unknown");
    }

    private static (double Score, bool Compatible, bool Known) Grammar(Lexical a, Lexical b)
    {
        if (a.Forms.Count == 0 || b.Forms.Count == 0) return (.45, true, false);
        var plausibleIncomplete = false;
        var confirmed = false; var confirmedMoods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in a.Forms)
        foreach (var y in b.Forms)
        {
            var shared = x.Keys.Intersect(y.Keys).ToArray();
            if (shared.Any(k => x[k] != y[k])) continue;
            if (Complete(x) && Complete(y) && x.Keys.All(y.ContainsKey) && y.Keys.All(x.ContainsKey))
            {
                confirmed = true;
                if (x.GetValueOrDefault("category") == "verb" && x.GetValueOrDefault("shape") == "finite")
                    confirmedMoods.Add(x["mood"]);
            }
            plausibleIncomplete = true;
        }
        if (confirmed)
        {
            // Homographs can expose different moods, e.g. past plural vs imperative singular.
            // A dictionary match to only one branch does not prove which reading the sentence uses.
            var sourceMoods = a.Forms.Where(Complete).Where(f => f.GetValueOrDefault("category") == "verb"
                && f.GetValueOrDefault("shape") == "finite").Select(f => f["mood"]).Distinct().ToArray();
            if (sourceMoods.Length > 1 && sourceMoods.Any(m => !confirmedMoods.Contains(m))) return (.45, true, false);
            return (1, true, true);
        }
        return plausibleIncomplete ? (.45, true, false) : (0, false, true);
    }

    private static bool Complete(Dictionary<string, string> form)
    {
        bool Has(params string[] keys) => keys.All(form.ContainsKey);
        if (!form.TryGetValue("category", out var category)) return false;
        return category switch
        {
            "noun" => Has("number", "case"),
            "adjective" => Has("shape", "number") && (form["shape"] == "short"
                ? form["number"] == "pl" || Has("gender")
                : Has("case") && (form["number"] == "pl" || Has("gender"))),
            "verb" => Has("shape") && (form["shape"] switch
            {
                "finite" => Has("mood", "number") && (form["mood"] == "imperative" ? Has("person")
                    : Has("tense") && (form["tense"] == "past"
                        ? form["number"] == "pl" || Has("gender") : Has("person"))),
                "participle" => Has("tense", "number", "case") && (form["number"] == "pl" || Has("gender")),
                "gerund" => Has("tense"), _ => false
            }),
            _ => false
        };
    }

    private static string Category(JsonElement data)
    {
        var tags = Array(data, "forms").SelectMany(f => Strings(f, "tags")).ToArray();
        if (Get(data, "aspect") is not null || tags.Any(t => t.StartsWith("presfut", StringComparison.Ordinal)
            || t.StartsWith("past_", StringComparison.Ordinal) || t.StartsWith("imperative_", StringComparison.Ordinal)
            || t is "infinitive" or "present" or "past" or "future" or "imperative" or "participle")) return "verb";
        if (Get(data, "gender") is not null || Get(data, "indeclinable") is not null) return "noun";
        if (tags.Any(t => t.StartsWith("decl_", StringComparison.Ordinal) || t.StartsWith("short_", StringComparison.Ordinal)
            || t is "comparative" or "superlative" or "short-form")) return "adjective";
        if (tags.Any(t => t is "masculine" or "feminine" or "neuter")) return "adjective";
        return tags.Any(t => t.StartsWith("sg_", StringComparison.Ordinal) || t is "nominative" or "genitive") ? "noun" : "";
    }

    private static Dictionary<string, string> Tags(string[] tags, string category)
    {
        var result = new Dictionary<string, string>(); if (category.Length > 0) result["category"] = category;
        if (category == "adjective") result["shape"] = "full";
        if (category == "verb") { result["shape"] = "finite"; result["mood"] = "indicative"; }
        foreach (var tag in tags)
        {
        if ((tag.StartsWith("decl_", StringComparison.Ordinal) || tag.StartsWith("short_", StringComparison.Ordinal)
            || tag.StartsWith("past_", StringComparison.Ordinal))
            && Regex.IsMatch(tag, "_(m|f|n)(_|$)")) result["number"] = "sg";
        foreach (var token in tag.Split('_'))
        {
            var pair = token switch
            {
                "sg" or "singular" => ("number", "sg"), "pl" or "plural" => ("number", "pl"),
                "nom" or "nominative" => ("case", "nom"), "gen" or "genitive" => ("case", "gen"),
                "dat" or "dative" => ("case", "dat"), "acc" or "accusative" => ("case", "acc"),
                "inst" or "instrumental" => ("case", "inst"), "prep" or "prepositional" => ("case", "prep"),
                "m" or "masculine" => ("gender", "m"), "f" or "feminine" => ("gender", "f"), "n" or "neuter" => ("gender", "n"),
                "present" or "presfut" => ("tense", "present"), "past" => ("tense", "past"), "future" => ("tense", "future"),
                "imperative" => ("mood", "imperative"), "short" or "short-form" => ("shape", "short"),
                "comparative" => ("degree", "comparative"), "superlative" => ("degree", "superlative"),
                "first-person" => ("person", "1"), "second-person" => ("person", "2"), "third-person" => ("person", "3"), _ => ("", "")
            };
            if (pair.Item1.Length > 0) result[pair.Item1] = pair.Item2;
            if (Regex.IsMatch(token, "^(sg|pl)[123]$")) { result["number"] = token[..2]; result["person"] = token[2..]; }
        }
        }
        if (category == "verb" && tags.Contains("past"))
        {
            var genders = tags.Count(t => t is "masculine" or "feminine" or "neuter");
            if (genders == 1) result.TryAdd("number", "sg");
            if (genders > 1) { result["number"] = "pl"; result.Remove("gender"); }
        }
        if (category == "verb")
        {
            if (tags.Contains("participle")) result["shape"] = tags.Contains("adverbial") ? "gerund" : "participle";
            if (tags.Contains("infinitive")) result["shape"] = "infinitive";
            if (result.GetValueOrDefault("mood") == "imperative") result["person"] = "2";
        }
        if (tags.Contains("animate") != tags.Contains("inanimate")) result["animate"] = tags.Contains("animate") ? "1" : "0";
        return result;
    }

    private static bool Proper(RagLexiconHit hit) => hit.Word.Any(char.IsLetter) && char.IsUpper(hit.Word.First(char.IsLetter));
    private static bool Restricted(RagLexiconHit hit)
    {
        var senses = Array(hit.Data, "senses").ToArray();
        return senses.Length > 0 && senses.All(s => Strings(s, "tags").Any(t => t is "archaic" or "obsolete" or "dialectal"
            or "nonstandard" or "rare" or "regional" or "misspelling" or "obscure")
            || Strings(s, "glosses").Any(g => Regex.IsMatch(g, @"^\s*(?:устар\.|истор\.|диал\.|рег\.|обл\.|редк\.|простореч\.)", RegexOptions.IgnoreCase)));
    }
    private static string Normalize(string text) => RagLexiconReader.Normalize(text);
    private static bool SingleWord(string word) => Regex.IsMatch(word, @"^[\p{L}\p{M}]+$");
    private static IEnumerable<string> Words(string text) => Regex.Matches(Normalize(text), @"[\p{L}]{3,}")
        .Select(m => m.Value).Where(w => !Stop.Contains(w));
    private static IEnumerable<JsonElement> Array(JsonElement data, string key) => data.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static IEnumerable<string> Strings(JsonElement data, string key) => Array(data, key)
        .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
    private static string? Get(JsonElement data, string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IEnumerable<string> Glosses(JsonElement data) => Strings(data, "definitions").Concat(Array(data, "senses").SelectMany(s => Strings(s, "glosses")));
}
