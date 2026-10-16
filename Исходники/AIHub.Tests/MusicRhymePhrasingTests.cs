using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymePhrasingTests
{
    [TestMethod]
    public void PossessiveMovesWithItsExistingCaseAndKeepsPunctuation()
    {
        const string source = "В ладонях твоих — тихий свет.";
        var result = Alternatives(source);
        Assert.IsTrue(result.Any(p => p.Text == "В твоих ладонях — тихий свет." && p.Reason == "PossessiveOrder"));
        Assert.AreEqual(MusicRhymePhonetics.RussianSyllables(source), MusicRhymePhonetics.RussianSyllables(result[0].Text));
        Assert.AreEqual(0, Alternatives("В ладонях твоей — тихий свет.").Length);
        Assert.AreEqual(0, Alternatives("В ладонях твоих сестёр — тихий свет.").Length);
        Assert.AreEqual(0, Alternatives("У друга его ждут.").Length);
    }

    [TestMethod]
    public void NominalCopulaCanBecomeCompactWithoutRemovingNegationOrComparison()
    {
        var negative = Alternatives("Музыка — это не тишина.");
        Assert.IsTrue(negative.Any(p => p.Text == "Музыка — не тишина." && p.Reason == "CopulaCompactness"));
        Assert.IsTrue(Alternatives("Музыка — это как тишина.").Any(p => p.Text == "Музыка — как тишина."));
        Assert.AreEqual(0, Alternatives("Это — это тишина.").Length);
        Assert.AreEqual(0, Alternatives("Музыка — это.").Length);
        Assert.AreEqual(0, Alternatives("Музыка — это она.").Length);
    }

    [TestMethod]
    public void AVerifiedLocativeMovesAsAWholeAndRetainsTheObjectAndEndingPunctuation()
    {
        var result = Alternatives("**Я читаю книгу в вагоне.**");
        Assert.IsTrue(result.Any(p => p.Text == "**В вагоне я читаю книгу.**" && p.Reason == "SceneFronting"));
        Assert.IsTrue(Alternatives("Я читаю книгу в тихом вагоне!").Any(p => p.Text == "В тихом вагоне я читаю книгу!"));
        // A different subject/verb order can leave an existing PP fault untouched; moving that PP is still forbidden.
        Assert.IsFalse(Alternatives("Я читаю книгу в тихой вагоне!").Any(p => p.Reason == "SceneFronting"));
        Assert.AreEqual(0, Alternatives("Я читает книгу в вагоне.").Length);
        Assert.AreEqual(0, Alternatives("Я читали книгу в вагоне.").Length);
        Assert.IsTrue(Alternatives("Мы читали книгу в вагоне.").Any(p => p.Text == "В вагоне мы читали книгу."));
        Assert.IsFalse(Alternatives("Я верю в чудо.").Any(p => p.Reason == "SceneFronting"));
        Assert.AreEqual(0, MusicRhymePhrasing.Alternatives(Line("Я читаю книгу в вагоне."), _ => []).Count());
    }

    [TestMethod]
    [DataRow("Му́зыка — это тишина.")]
    [DataRow("Музыка — это 'тишина'.")]
    [DataRow("Музыка — это «тишина».")]
    [DataRow("Музыка — это тишина (тихо).")]
    [DataRow("[Куплет] Музыка — это тишина.")]
    [DataRow("— Музыка — это тишина.")]
    [DataRow("Музыка: это тишина.")]
    public void StressesDirectionsAndDirectSpeechAreNotRewritten(string source) => Assert.AreEqual(0, Alternatives(source).Length);

    [TestMethod]
    public void EnglishIsNotShuffledAndStructuralCompactnessWorksWithoutADictionary()
    {
        Assert.AreEqual(0, MusicRhymePhrasing.Alternatives(Line("I read a book on the train.") with { Language = "en" }, Lookup).Count());
        Assert.IsTrue(MusicRhymePhrasing.Alternatives(Line("Музыка — это тишина."), null).Any(p => p.Text == "Музыка — тишина."));
    }

    [TestMethod]
    public void AVerifiedPronounCanFollowItsPredicateWithoutLosingNegationOrTheNextClause()
    {
        Assert.IsTrue(Alternatives("Мы не уснём, пока шумит вокзал.").Any(p => p.Text == "Не уснём мы, пока шумит вокзал." && p.Reason == "PoeticOrder"));
        Assert.IsFalse(Alternatives("Мы не уснёт, пока шумит вокзал.").Any(p => p.Reason == "PoeticOrder"));
        Assert.IsFalse(Alternatives("Я шум в вагоне.").Any(p => p.Reason == "PoeticOrder"));
    }

    [TestMethod]
    public void APronounsLocativeMovesTogetherBeforeTheSubject()
    {
        Assert.IsTrue(Alternatives("Она в тихом вагоне читает, пока идёт дождь.").Any(p => p.Text == "В тихом вагоне она читает, пока идёт дождь."));
        Assert.AreEqual(0, Alternatives("Она в тихой вагоне читает, пока идёт дождь.").Length);
        Assert.AreEqual(0, Alternatives("Мы в вагоне читает, пока идёт дождь.").Length);
    }

    [TestMethod]
    public void AnInitialWholeGroupKeepsItsGenitiveDependentWhenMovedAfterThePredicate()
    {
        Assert.IsTrue(Alternatives("За окнами вагона ветер шумит.").Any(p => p.Text == "Ветер шумит за окнами вагона." && p.Reason == "SceneReordering"));
        Assert.AreEqual(0, Alternatives("За окнами вагона ветер шумят.").Length);
        Assert.AreEqual(0, Alternatives("За окнами вагона ветер шум.").Length);
        Assert.AreEqual(0, Alternatives("За окнами вагона Пётр шумит.").Length);
    }

    private static MusicRhymePhrase[] Alternatives(string source) => MusicRhymePhrasing.Alternatives(Line(source), Lookup).ToArray();
    private static MusicRhymeLine Line(string text) => new(1, 0, text.Length, 0, text, "", 0, "ru", null, []);
    private static IReadOnlyList<RagLexiconHit> Lookup(string word)
    {
        var item = MusicRhymePhonetics.Clean(word) switch {
            "ладонях" => ("ладонь", "f", "pl_prep"), "твоих" => ("твой", "", "decl_pl_prep"),
            "твоей" => ("твой", "", "decl_f_prep"), "музыка" => ("музыка", "f", "sg_nom"),
            "тишина" => ("тишина", "f", "sg_nom"), "читаю" => ("читать", "", "presfut_sg1"),
            "читает" => ("читать", "", "presfut_sg3"), "верю" => ("верить", "", "presfut_sg1"),
            "читали" => ("читать", "", "past_masculine_feminine_neuter"),
            "уснём" => ("уснуть", "", "presfut_pl1"), "уснёт" => ("уснуть", "", "presfut_sg3"),
            "окнами" => ("окно", "n", "pl_inst"), "вагона" => ("вагон", "m", "sg_gen"),
            "ветер" => ("ветер", "m", "sg_nom"), "шумит" => ("шуметь", "", "presfut_sg3"),
            "шумят" => ("шуметь", "", "presfut_pl3"), "шум" => ("шум", "m", "sg_nom"), "пётр" => ("Пётр", "m", "sg_nom"),
            "друга" => ("друг", "m", "sg_gen"), "ждут" => ("ждать", "", "presfut_pl3"),
            "книгу" => ("книга", "f", "sg_acc"), "вагоне" => ("вагон", "m", "sg_prep"),
            "тихом" => ("тихий", "", "decl_m_prep"), "тихой" => ("тихий", "", "decl_f_prep"),
            "чудо" => ("чудо", "n", "sg_acc"), _ => ("", "", "") };
        if (item.Item1.Length == 0) return [];
        var data = JsonSerializer.SerializeToElement(new { gender = item.Item2,
            forms = new[] { new { form = word, tags = new[] { item.Item3 } } } });
        return [new(item.Item1, "forms", data, false, "fixture", "CC0", "test fixture", item.Item1)];
    }
}
