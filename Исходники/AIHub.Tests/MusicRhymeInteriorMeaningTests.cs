using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeInteriorMeaningTests
{
    [TestMethod]
    public void IndependentWholeTranslationUnitsConfirmAnOrdinaryLexicalVariation()
    {
        var lookup = Dictionary(
            ("сломал", Hit("сломать", "forms", """{"translations":["break, fracture, quarry"]}""")),
            ("поломал", Hit("поломать", "forms", """{"translations":["break"]}""")),
            ("мечты", Hit("мечта", "forms", """{"translations":["dream, day-dream, waking dream"]}""")),
            ("грёзы", Hit("грёза", "forms", """{"translations":["dream, daydream, reverie, vision"]}""")));
        Assert.IsTrue(MusicRhymeInteriorMeaning.Allows("сломал", "поломал", "Я сломал замок", lookup));
        Assert.IsTrue(MusicRhymeInteriorMeaning.Allows("мечты", "грёзы", "В сердце мои мечты", lookup));
    }

    [TestMethod]
    public void TranslationTokensCannotCreateAFictitiousMatchBetweenDifferentSenses()
    {
        var lookup = Dictionary(
            ("смысла", Hit("смысл", "forms", """{"translations":["sense, meaning"]}""")),
            ("рассудка", Hit("рассудок", "forms", """{"translations":["reason, intellect, mind, common sense"]}""")));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("смысла", "рассудка", "Не нахожу смысла", lookup));
    }

    [TestMethod]
    public void AnAmbiguousCandidateDoesNotBorrowTheMeaningOfOnlyOneLemma()
    {
        var lookup = Dictionary(
            ("живу", Hit("жить", "forms", """{"translations":["live"]}""")),
            ("вожусь", Hit("водиться", "forms", """{"translations":["associate, be found; live"]}""")),
            ("вожусь", Hit("возиться", "forms", """{"translations":["mess around, play around"]}""")));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("живу", "вожусь", "Я живу один", lookup));
    }

    [TestMethod]
    public void AnEntirelyArchaicCandidateCannotPassUsingAModernTranslation()
    {
        var lookup = Dictionary(
            ("смотрели", Hit("смотреть", "forms", """{"translations":["look, watch"]}""")),
            ("взирали", Hit("взирать", "forms", """{"translations":["look, gaze"]}""")),
            ("взирали", Hit("взирать", "meaning", """{"senses":[{"glosses":["высок., устар. смотреть, глядеть"]}]}""")));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("смотрели", "взирали", "Мы смотрели фильм", lookup));
    }

    [TestMethod]
    public void DiminutiveNeedsAnExplicitLemmaRelationAndAnIndependentTranslation()
    {
        var lookup = Dictionary(
            ("снег", Hit("снег", "forms", """{"translations":["snow"]}""")),
            ("снежок", Hit("снежок", "forms", """{"translations":["light snow, snowball, snowball fight"]}""")),
            ("снежок", Hit("снежок", "meaning", """{"senses":[{"glosses":["уменьш.-ласк. к снег"]}]}""")),
            ("снежочек", Hit("снежочек", "meaning", """{"senses":[{"glosses":["уменьш.-ласк. к снег"]}]}""")));
        Assert.IsTrue(MusicRhymeInteriorMeaning.Allows("снег", "снежок", "Падает снег", lookup));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("снег", "снежочек", "Падает снег", lookup));
    }

    [TestMethod]
    public void FlattenedSynonymsAndSparseUnknownEntriesAreNotEnough()
    {
        var lookup = Dictionary(
            ("мерцают", Hit("мерцать", "forms", """{"translations":["twinkle, shimmer, glimmer, flicker"]}""")),
            ("мерцают", Hit("мерцать", "meaning", """{"synonyms":["вибрировать"],"senses":[{"glosses":["светиться неровным светом"]},{"glosses":["биол. производить колебательные движения; вибрировать"]}]}""")),
            ("вибрируют", Hit("вибрировать", "forms", """{"translations":["vibrate"]}""")));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("мерцают", "вибрируют", "Мерцают огни", lookup));
        Assert.IsFalse(MusicRhymeInteriorMeaning.Allows("неизвестно", "неясно", "Неизвестно почему", lookup));
    }

    private static RagLexiconHit Hit(string word, string kind, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(word, kind, document.RootElement.Clone(), false, "fixture", "CC0", "test fixture", word);
    }
    private static Func<string, IReadOnlyList<RagLexiconHit>> Dictionary(params (string Surface, RagLexiconHit Hit)[] items)
    {
        var map = items.GroupBy(i => RagLexiconReader.Normalize(i.Surface)).ToDictionary(g => g.Key,
            g => (IReadOnlyList<RagLexiconHit>)g.Select(i => i.Hit).ToArray());
        return word => map.GetValueOrDefault(RagLexiconReader.Normalize(word), []);
    }
}
