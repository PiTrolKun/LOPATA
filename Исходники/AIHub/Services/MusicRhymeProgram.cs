using AIHub.Models;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public sealed record MusicRhymeProgramDraft(string Text, MusicRhymeProgramChange[] Changes, MusicRhymeSuggestion[] Candidates);

/// <summary>Bounded editorial alternatives. The original and the main editor are never changed.</summary>
public static class MusicRhymeProgram
{
    private sealed record Proposal(MusicRhymeLine Line, string Text, string Reason, double Score,
        double Before, double After, string Relation = "", bool GrammarKnown = false);

    public static string Build(MusicRhymeStep step, IReadOnlyList<MusicRhymeSuggestion> candidates,
        Func<string, IReadOnlyList<RagLexiconHit>>? lookup = null) => BuildDetailed(step, candidates, lookup).Text;

    public static MusicRhymeProgramDraft BuildDetailed(MusicRhymeStep step, IReadOnlyList<MusicRhymeSuggestion> candidates,
        Func<string, IReadOnlyList<RagLexiconHit>>? lookup = null, CancellationToken token = default)
    {
        if (step.Analysis is null) return new(step.Request.Text, [], []);
        lookup ??= _ => [];
        var tuning = step.Request.Tuning;
        token.ThrowIfCancellationRequested();
        var selected = MusicRhymeEditorial.ForScope(step, token);
        var lines = step.Analysis.Lines.ToDictionary(l => l.Number);
        var preserved = selected.PreserveRhymePairs.SelectMany(pair => pair.Select(number => (number, Others: pair.Where(n => n != number))))
            .GroupBy(p => p.number).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Others).Distinct().Select(n => lines[n]).ToArray());
        var accepted = new List<MusicRhymeSuggestion>(); var edits = new List<Proposal>();
        foreach (var fragment in selected.Fragments)
        {
            token.ThrowIfCancellationRequested(); var proposals = new List<Proposal>();
            foreach (var number in fragment.Lines)
            {
                if (!lines.TryGetValue(number, out var line) || !Editable(step, line)) continue;
                var partner = fragment.Lines.Where(n => n != number).Select(n => lines[n])
                    .OrderBy(l => Math.Abs(l.Number - number)).FirstOrDefault();
                var before = Sound(line.Readings, partner?.Readings, tuning);
                var endingAllowed = MusicRhymeSyntax.CanReplace(line, line.EndingStart - line.Start, line.Ending.Length, lookup, tuning);
                foreach (var candidate in candidates.Where(c => endingAllowed && c.Line == number).DistinctBy(c => c.Word).Take(tuning.Integer("program.candidates_per_line")))
                {
                    token.ThrowIfCancellationRequested();
                    if (MusicRhymePhonetics.Clean(candidate.Word) == MusicRhymePhonetics.Clean(line.Ending)
                        || ClosedClass(line.Ending) || Reciprocal(line.Text, line.EndingStart - line.Start)) continue;
                    var fit = MusicRhymeWordEvidence.Evaluate(line.Ending, candidate.Word, line.Text, lookup, tuning);
                    if (!fit.Compatible || fit.Meaning <= 0 || fit.Relation == "polysemous-synonym"
                        || fit.Meaning < tuning.Number("quality.minimum_meaning") && fit.Relation != "synonym-bridge") continue;
                    // Sparse uninflected entries may support an image shift, but never pretend their grammar was checked.
                    if (!fit.GrammarKnown && line.Language != "en"
                        && !(fit.Relation == "synonym-bridge" && Invariant(line.Ending, lookup) && Invariant(candidate.Word, lookup))) continue;
                    var readings = MusicRhymePhonetics.Readings(candidate.Word, lookup(candidate.Word));
                    var after = Sound(readings, partner?.Readings, tuning);
                    var delta = SyllableDifference(line.Ending, candidate.Word, line.Language, line.Readings, readings);
                    if (delta > tuning.Integer("quality.ending_syllable_difference") || !PreservesKnownPairs(line, readings)) continue;
                    var improves = partner is not null && after >= tuning.Number("quality.near_similarity") && after >= before + tuning.Number("quality.sound_improvement");
                    var semantic = fit.Meaning >= tuning.Number("quality.strong_meaning") && after >= before - tuning.Number("quality.semantic_sound_loss");
                    if (!improves && !semantic) continue;
                    if (before >= tuning.Number("quality.near_similarity") && after < before - tuning.Number("quality.good_rhyme_loss")) continue;
                    var score = 30 * fit.Meaning + 15 * fit.Grammar + 20 * after - 3 * delta + (improves ? 45 : 0);
                    var ranked = candidate with { MeaningScore = fit.Meaning, GrammarScore = fit.Grammar,
                        GrammarKnown = fit.GrammarKnown, Relation = fit.Relation, SoundScore = after, Score = score,
                        Synonym = fit.Relation is "synonym" or "synset" };
                    accepted.Add(ranked);
                    var word = Capitalize(candidate.Word, line.Ending);
                    proposals.Add(new(line, line.Text.Remove(line.EndingStart - line.Start, line.Ending.Length).Insert(line.EndingStart - line.Start, word),
                        improves ? after >= tuning.Number("quality.exact_similarity") ? "ExactMeaning" : "NearMeaning" : "Semantic", score, before, after, fit.Relation, fit.GrammarKnown));
                }
                if (partner is not null && Inversion(line, partner, lookup, tuning) is { } inversion)
                    AddPhrase(inversion, "RhymeWordOrder", 85);
                foreach (var phrase in MusicRhymePhrasing.Alternatives(line, lookup, tuning))
                    AddPhrase(phrase.Text, phrase.Reason, phrase.Reason == "PoeticOrder" ? 65 : 70);

                // Interior lexical variation keeps the established ending and surrounding syntax.
                foreach (Match word in Regex.Matches(line.Text, @"[\p{L}\p{M}]+").Take(tuning.Integer("program.interior_words")))
                {
                    if (word.Length < tuning.Integer("program.interior_min_letters") || line.Start + word.Index >= line.EndingStart || ClosedClass(word.Value)
                        || Reciprocal(line.Text, word.Index) || !MusicRhymeSyntax.CanReplace(line, word.Index, word.Length, lookup, tuning)) continue;
                    foreach (var alternative in MusicRhymeVocabulary.Related(word.Value, lookup, token, tuning).Take(tuning.Integer("program.interior_candidates")))
                    {
                        token.ThrowIfCancellationRequested();
                        var fit = MusicRhymeWordEvidence.Evaluate(word.Value, alternative, line.Text, lookup, tuning);
                        if (!fit.Compatible || !fit.GrammarKnown || fit.Meaning < tuning.Number("quality.interior_meaning")) continue;
                        if (!MusicRhymeInteriorMeaning.Allows(word.Value, alternative, line.Text, lookup)) continue;
                        var readings = MusicRhymePhonetics.Readings(alternative, lookup(alternative));
                        if (SyllableDifference(word.Value, alternative, line.Language,
                            MusicRhymePhonetics.Readings(word.Value, lookup(word.Value)), readings) > tuning.Integer("quality.interior_syllable_difference")) continue;
                        var text = line.Text.Remove(word.Index, word.Length).Insert(word.Index, Capitalize(alternative, word.Value));
                        proposals.Add(new(line, text, "InnerSemantic", 20 + 12 * fit.Meaning, before, before, fit.Relation, true));
                    }
                }
                void AddPhrase(string text, string reason, double score)
                {
                    if (text == line.Text) return;
                    var last = Regex.Matches(text, @"[\p{L}\p{M}]+").LastOrDefault();
                    if (last is null) return;
                    var readings = MusicRhymePhonetics.Readings(last.Value, lookup(last.Value));
                    var after = Sound(readings, partner?.Readings, tuning);
                    if (!PreservesKnownPairs(line, readings)
                        || before >= tuning.Number("quality.near_similarity") && after < before - tuning.Number("quality.good_rhyme_loss")) return;
                    proposals.Add(new(line, text, reason, score, before, after));
                }
            }
            var best = proposals.OrderByDescending(p => p.Score).ThenBy(p => p.Line.Number)
                .ThenBy(p => p.Text, StringComparer.Ordinal).Where(p => !edits.Any(e => e.Line.Number == p.Line.Number))
                .DistinctBy(p => p.Line.Number).Take(tuning.Integer("program.changes_per_fragment"))
                .Take(Math.Max(0, tuning.Integer("program.total_changes") - edits.Count)).ToArray();
            edits.AddRange(best);
        }
        bool PreservesKnownPairs(MusicRhymeLine line, MusicRhymeReading[] after) =>
            !preserved.TryGetValue(line.Number, out var partners)
            || partners.All(p => Sound(after, p.Readings, tuning) >= Sound(line.Readings, p.Readings, tuning) - tuning.Number("quality.preserved_pair_loss"));
        // Each fragment retains its own bounded alternatives; unrelated fragments do not compete for a single slot.
        var outputText = step.Request.Text;
        foreach (var edit in edits.OrderByDescending(e => e.Line.Start))
            outputText = outputText.Remove(edit.Line.Start - step.Request.Start, edit.Line.Length).Insert(edit.Line.Start - step.Request.Start, edit.Text);
        return new(outputText, edits.Select(e => new MusicRhymeProgramChange(e.Line.Number, e.Line.Text, e.Text,
            e.Reason, e.Before, e.After, e.Relation, e.GrammarKnown)).ToArray(),
            accepted.OrderByDescending(s => s.Score).DistinctBy(s => (s.Line, s.Word)).Take(tuning.Integer("program.saved_candidates")).ToArray());
    }
    private static bool Editable(MusicRhymeStep step, MusicRhymeLine line) => !line.Performance
        && line.Start >= step.Request.Start && line.Start + line.Length <= step.Request.Start + step.Request.Length
        && !line.Text.Any(c => c is '\u0301' or '\u0300' or '+' or '\'' or '’' or '‘' or '"' or '«' or '»'
            or '“' or '”' or '[' or ']' or '(' or ')' or '{' or '}');

    private static double Sound(IEnumerable<MusicRhymeReading> a, IEnumerable<MusicRhymeReading>? b, MusicRhymeTuning tuning) => b is null ? 0
        : a.Where(r => !r.Uncertain).SelectMany(r => b.Where(p => !p.Uncertain).Select(p => MusicRhymeSound.Similarity(r, p, tuning))).DefaultIfEmpty().Max();

    private static int SyllableDifference(string a, string b, string language, MusicRhymeReading[] before, MusicRhymeReading[] after)
    {
        var x = before.FirstOrDefault()?.Syllables ?? (language == "ru" ? MusicRhymePhonetics.RussianSyllables(a) : -100);
        var y = after.FirstOrDefault()?.Syllables ?? (language == "ru" ? MusicRhymePhonetics.RussianSyllables(b) : 100);
        return Math.Abs(x - y);
    }
    private static string Capitalize(string word, string original) => original.All(char.IsUpper) ? word.ToUpperInvariant()
        : char.IsUpper(original[0]) ? char.ToUpperInvariant(word[0]) + word[1..] : word;

    private static bool ClosedClass(string word) => Regex.IsMatch(word, @"^(?:это[тмг]?|этого|этой|этим|этих|того|тому|теми|меня|мене|тебя|тебе|себя|себе|нами|вами|него|нему|неё|нею|ними|котор\p{L}*|свой|своя|свои|моей|моим|моих|тво\p{L}*|наш\p{L}*|ваш\p{L}*|кажд\p{L}*)$", RegexOptions.IgnoreCase);

    private static bool Reciprocal(string text, int at) => Regex.Matches(text,
        @"\bдруг\s+(?:(?:в|на|о|от|с|к|ко|для|за)\s+)?друг(?:а|у|ом|е)?\b", RegexOptions.IgnoreCase)
        .Any(m => at >= m.Index && at < m.Index + m.Length);

    private static bool Invariant(string word, Func<string, IReadOnlyList<RagLexiconHit>> lookup)
    {
        return lookup(word).Any(h => h.Kind == "forms" && MusicRhymePhonetics.Clean(h.Word) == MusicRhymePhonetics.Clean(word)
            && !h.Data.TryGetProperty("gender", out _) && !h.Data.TryGetProperty("aspect", out _)
            && h.Data.TryGetProperty("forms", out var forms) && forms.ValueKind == System.Text.Json.JsonValueKind.Array && forms.GetArrayLength() == 0);
    }

    private static string? Inversion(MusicRhymeLine line, MusicRhymeLine partner, Func<string, IReadOnlyList<RagLexiconHit>> lookup, MusicRhymeTuning tuning)
    {
        if (line.Language != "ru") return null;
        var group = Regex.Match(line.Text, @"(?<verb>[\p{L}]+)\s+(?<subject>(?:мой|моя|моё|мои|твой|твоя|твоё|твои|наш|наша|наше|наши|ваш|ваша|ваше|ваши)\s+[\p{L}]+)(?<tail>[.!?,…]*)$");
        if (!group.Success || group.Index == 0) return null;
        var verb = group.Groups["verb"].Value;
        if (!Regex.IsMatch(verb, @"(?:[её]т|[ую]т|[яа]т|ит|ил|ал|ла|ли|лю)$", RegexOptions.IgnoreCase)) return null;
        if (Sound(MusicRhymePhonetics.Readings(verb, lookup(verb)), partner.Readings, tuning) < tuning.Number("quality.exact_similarity")) return null;
        return line.Text[..group.Index] + group.Groups["subject"].Value + " " + verb + group.Groups["tail"].Value;
    }
}
