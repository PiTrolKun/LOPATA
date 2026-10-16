using AIHub.Models;

namespace AIHub.Services;

/// <summary>Bounded phonetic pool plus direct lexical forms, followed by semantic and grammatical ranking.</summary>
public static class MusicRhymeCandidateSearch
{
    public static MusicRhymeProgramDraft Build(string index, MusicRhymeStep step, MusicRhymeLexicon lexicon,
        CancellationToken token = default)
    {
        var candidates = new List<MusicRhymeSuggestion>();
        token.ThrowIfCancellationRequested();
        var selected = MusicRhymeEditorial.ForScope(step, token);
        var lines = step.Analysis!.Lines.ToDictionary(l => l.Number);
        foreach (var fragment in selected.Fragments)
        foreach (var number in fragment.Lines)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[number];
            var partner = fragment.Lines.Where(n => n != number).Select(n => lines[n])
                .OrderBy(l => Math.Abs(l.Number - number)).FirstOrDefault();
            if (partner is not null)
                candidates.AddRange(MusicRhymeIndex.Find(index, line, partner, lexicon.Synonyms(line.Ending), token,
                    word => lexicon.Lookup(word).Any(h => h.Source.StartsWith("openrussian-", StringComparison.Ordinal)
                        || h.Source.StartsWith("english-wordnet-", StringComparison.Ordinal)), step.Request.Tuning));
            candidates.AddRange(MusicRhymeVocabulary.EndingCandidates(line, lexicon.Lookup, token, step.Request.Tuning));
        }
        return MusicRhymeProgram.BuildDetailed(step, candidates, lexicon.Lookup, token);
    }
}
