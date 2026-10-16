using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeWordEvidenceTests
{
    [TestMethod]
    public void InflectedSynonymNeedsCompatibleCaseNumberAndAdjectiveGender()
    {
        var dictionary = Dictionary(
            ("тихим", Hit("тихий", "forms", """{"forms":[{"form":"ти́хим","tags":["decl_m_inst"]}]}""")),
            ("тихим", Hit("тихий", "meaning", """{"synonyms":["негромкий"],"senses":[{"glosses":["звучащий слабо"]}]}""")),
            ("негромким", Hit("негромкий", "forms", """{"forms":[{"form":"негро́мким","tags":["singular","masculine","instrumental"]}]}""")),
            ("негромкую", Hit("негромкий", "forms", """{"forms":[{"form":"негро́мкую","tags":["singular","feminine","accusative"]}]}""")),
            ("негромкими", Hit("негромкий", "forms", """{"forms":[{"form":"негро́мкими","tags":["plural","instrumental"]}]}""")));
        var suitable = MusicRhymeWordEvidence.Evaluate("тихим", "негромким", "Он ответил тихим голосом", dictionary);
        Assert.AreEqual(1, suitable.Meaning); Assert.IsTrue(suitable.Compatible); Assert.IsTrue(suitable.GrammarKnown);
        var wrong = MusicRhymeWordEvidence.Evaluate("тихим", "негромкую", "Он ответил тихим голосом", dictionary);
        Assert.IsFalse(wrong.Compatible); Assert.AreEqual(0, wrong.Grammar);
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("тихим", "негромкими", "Он ответил тихим голосом", dictionary).Compatible);
    }

    [TestMethod]
    public void InstrumentalCannotBecomeNominativeEvenWhenTheOtherWordRhymes()
    {
        var dictionary = Dictionary(
            ("недоверием", Hit("недоверие", "forms", """{"gender":"n","animate":"0","forms":[{"form":"недове́рием","tags":["sg_inst"]}]}""")),
            ("недоверием", Hit("недоверие", "meaning", """{"synonyms":["сомнение","подозрение"],"senses":[{"glosses":["сомнение в правдивости"]}]}""")),
            ("башибузук", Hit("башибузук", "forms", """{"gender":"m","animate":"1","forms":[{"form":"башибузу́к","tags":["sg_nom"]}]}""")),
            ("башибузук", Hit("башибузук", "meaning", """{"synonyms":["разбойник","головорез"],"senses":[{"glosses":["солдат турецких войск"]}]}""")));
        var result = MusicRhymeWordEvidence.Evaluate("недоверием", "башибузук", "Он смотрит на меня с недоверием", dictionary);
        Assert.AreEqual(0, result.Meaning); Assert.IsFalse(result.Compatible); Assert.IsTrue(result.GrammarKnown);
    }

    [TestMethod]
    public void DocumentedSynonymTranslationBridgeAllowsAnImageShiftWithoutClaimingExactSynonymy()
    {
        // These sparse fields mirror the installed OpenRussian and Kaikki records.
        var dictionary = Dictionary(
            ("вдаль", Hit("вдаль", "forms", """{"forms":[],"translations":["far"]}""")),
            ("вдаль", Hit("вдаль", "meaning", """{"synonyms":["далеко"],"senses":[{"glosses":["на далёкое расстояние"]}]}""")),
            ("далеко", Hit("далеко", "forms", """{"forms":[],"translations":["far off, a great distance away"]}""")),
            ("прочь", Hit("прочь", "forms", """{"forms":[],"translations":["away, off"]}""")));
        var result = MusicRhymeWordEvidence.Evaluate("вдаль", "прочь", "Ветер гонит меня вдаль", dictionary);
        Assert.AreEqual(.55, result.Meaning); Assert.AreEqual("synonym-bridge", result.Relation);
        Assert.IsTrue(result.Compatible); Assert.IsFalse(result.GrammarKnown); Assert.AreEqual(.45, result.Grammar);
    }

    [TestMethod]
    public void SpellingOrRhymingWithoutLexicalEvidenceNeverBecomesMeaning()
    {
        var result = MusicRhymeWordEvidence.Evaluate("дорога", "тревога", "Нас ждёт дорога", _ => []);
        Assert.AreEqual(0, result.Meaning); Assert.AreEqual("unknown", result.Relation);
        Assert.IsFalse(result.GrammarKnown);
    }

    [TestMethod]
    public void ANameOnlyCandidateIsRejectedButACommonHomonymRemainsAvailable()
    {
        var dictionary = Dictionary(
            ("пора", Hit("Пора", "meaning", """{"senses":[{"glosses":["мужское имя"]}],"synonyms":[]}""")),
            ("игра", Hit("Игра", "meaning", """{"senses":[{"glosses":["посёлок городского типа"]}]}""")),
            ("игра", Hit("игра", "meaning", """{"senses":[{"glosses":["развлекательное занятие"]}],"synonyms":["забава"]}""")),
            ("забава", Hit("забава", "meaning", """{"senses":[{"glosses":["развлечение"]}],"synonyms":["игра"]}""")));
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("игра", "пора", "Началась игра", dictionary).Compatible);
        Assert.AreEqual(1, MusicRhymeWordEvidence.Evaluate("забава", "игра", "Началась забава", dictionary).Meaning);
    }

    [TestMethod]
    public void EnglishSynsetProvidesMeaningAndPartOfSpeechButNotAnInventedInflectionCheck()
    {
        var dictionary = Dictionary(
            ("dream", Hit("dream", "meaning", """{"synset":"05776550-n","definitions":["mental images during sleep"],"synonyms":["dream","dreaming"]}""")),
            ("dreaming", Hit("dream", "meaning", """{"synset":"05776550-n","definitions":["mental images during sleep"],"synonyms":["dream","dreaming"]}""")),
            ("run", Hit("run", "meaning", """{"synset":"01926329-v","definitions":["move fast"],"synonyms":["run"]}""")));
        var fit = MusicRhymeWordEvidence.Evaluate("dream", "dreaming", "I keep a dream", dictionary);
        Assert.AreEqual(1, fit.Meaning); Assert.IsTrue(fit.Compatible); Assert.IsFalse(fit.GrammarKnown);
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("dream", "run", "I keep a dream", dictionary).Compatible);
    }

    [TestMethod]
    public void ADefinitionReferenceIsUsefulWithoutAnExplicitSynonymList()
    {
        var dictionary = Dictionary(
            ("час", Hit("час", "meaning", """{"senses":[{"glosses":["время определённого события"]}]}""")),
            ("время", Hit("время", "meaning", """{"senses":[{"glosses":["период существования"]}]}""")));
        var fit = MusicRhymeWordEvidence.Evaluate("час", "время", "Пришёл наш час", dictionary);
        Assert.AreEqual(.7, fit.Meaning); Assert.AreEqual("meaning-reference", fit.Relation);
    }

    [TestMethod]
    public void CompactPastAndLongPluralTagsCannotSilentlyChangeVerbNumber()
    {
        var dictionary = Dictionary(
            ("пел", Hit("петь", "forms", """{"aspect":"imperfective","forms":[{"form":"пе́л","tags":["past_m"]}]}""")),
            ("звучал", Hit("звучать", "meaning", """{"forms":[{"form":"звуча́л","tags":["past","masculine"]}]}""")),
            ("звучали", Hit("звучать", "meaning", """{"forms":[{"form":"звуча́ли","tags":["past","masculine","feminine","neuter"]}]}""")));
        Assert.IsTrue(MusicRhymeWordEvidence.Evaluate("пел", "звучал", "Мой голос пел", dictionary).Compatible);
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("пел", "звучали", "Мой голос пел", dictionary).Compatible);
    }

    [TestMethod]
    public void FlattenedPolysemousVerbSynonymIsNotStrongEvidenceForAnInteriorReplacement()
    {
        // The dictionary flattens literal looking and having an opinion into the same synonym array.
        var dictionary = Dictionary(
            ("смотрит", Hit("смотреть", "forms", """{"translations":["look, watch"],"aspect":"imperfective","forms":[{"form":"смо́трит","tags":["presfut_sg3"]}]}""")),
            ("смотрит", Hit("смотреть", "meaning", """{"synonyms":["считать","глядеть"],"senses":[{"glosses":["обращать глаза"]},{"glosses":["быть зрителем"]},{"glosses":["иметь мнение"]},{"glosses":["заботиться"]}]}""")),
            ("считает", Hit("считать", "forms", """{"translations":["consider, think, count"],"aspect":"imperfective","forms":[{"form":"счита́ет","tags":["presfut_sg3"]}]}""")),
            ("глядит", Hit("глядеть", "forms", """{"translations":["look, peer"],"aspect":"imperfective","forms":[{"form":"гляди́т","tags":["presfut_sg3"]}]}""")));
        var unsafeSense = MusicRhymeWordEvidence.Evaluate("смотрит", "считает", "Он смотрит на меня с недоверием", dictionary);
        Assert.IsTrue(unsafeSense.GrammarKnown); Assert.IsTrue(unsafeSense.Compatible);
        Assert.AreEqual(.7, unsafeSense.Meaning); Assert.AreEqual("polysemous-synonym", unsafeSense.Relation);
        var supported = MusicRhymeWordEvidence.Evaluate("смотрит", "глядит", "Он смотрит на меня с недоверием", dictionary);
        Assert.AreEqual(1, supported.Meaning); Assert.AreEqual("synonym", supported.Relation);
    }

    [TestMethod]
    public void OneSharedTranslationIsAnAssociationRatherThanAConfirmedContextualSynonym()
    {
        var dictionary = Dictionary(
            ("свет", Hit("свет", "forms", """{"translations":["light, world, society"]}""")),
            ("мир", Hit("мир", "forms", """{"translations":["world, peace"]}""")));
        var result = MusicRhymeWordEvidence.Evaluate("свет", "мир", "Свет улицы погас", dictionary);
        Assert.AreEqual(.6, result.Meaning); Assert.AreEqual("shared-translation", result.Relation);
    }

    [TestMethod]
    public void SparseMeaningFormsCannotBypassGenderKnownFromTheSameLemmasFormRecords()
    {
        var dictionary = Dictionary(
            ("место", Hit("место", "forms", """{"gender":"n","animate":"0","forms":[{"form":"ме́сто","tags":["sg_nom"]}]}""")),
            ("место", Hit("место", "meaning", """{"forms":[{"form":"ме́сто","tags":["singular","nominative"]}]}""")),
            ("костыль", Hit("костыль", "forms", """{"gender":"m","animate":"0","forms":[{"form":"косты́ль","tags":["sg_nom"]}]}""")),
            ("костыль", Hit("костыль", "meaning", """{"forms":[{"form":"косты́ль","tags":["singular","nominative"]}]}""")));
        var result = MusicRhymeWordEvidence.Evaluate("место", "костыль", "Не моё место", dictionary);
        Assert.IsFalse(result.Compatible); Assert.AreEqual(0, result.Grammar);
    }

    [TestMethod]
    public void CompleteVerbFormsDistinguishTenseMoodAndGerundsEvenWhenOneNumberMatches()
    {
        var dictionary = Dictionary(
            ("ушли", Hit("уйти", "forms", """{"aspect":"perfective","forms":[{"form":"ушли́","tags":["past_pl"]}]}""")),
            ("ушли", Hit("услать", "forms", """{"aspect":"perfective","forms":[{"form":"ушли́","tags":["imperative_sg"]}]}""")),
            ("говеет", Hit("говеть", "forms", """{"aspect":"imperfective","forms":[{"form":"гове́ет","tags":["presfut_sg3"]}]}""")),
            ("превратил", Hit("превратить", "forms", """{"aspect":"perfective","forms":[{"form":"преврати́л","tags":["past_m"]}]}""")),
            ("претворив", Hit("претворить", "meaning", """{"forms":[{"form":"претвори́в","tags":["adverbial","participle","past"]}]}""")),
            ("уйду", Hit("уйти", "forms", """{"aspect":"perfective","forms":[{"form":"уйду́","tags":["presfut_sg1"]}]}""")),
            ("уволься", Hit("уволиться", "meaning", """{"forms":[{"form":"уво́лься","tags":["imperative","second-person"]}]}""")));
        foreach (var (before, after) in new[] { ("ушли", "говеет"), ("превратил", "претворив"), ("уйду", "уволься") })
            Assert.IsFalse(MusicRhymeWordEvidence.Evaluate(before, after, before, dictionary).Compatible, before + " → " + after);
    }

    [TestMethod]
    public void CompactPerfectivePresfutIsFutureAndDoesNotMatchPresent()
    {
        var dictionary = Dictionary(
            ("пойдут", Hit("пойти", "forms", """{"aspect":"perfective","forms":[{"form":"пойду́т","tags":["presfut_pl3"]}]}""")),
            ("уйдут", Hit("уйти", "meaning", """{"forms":[{"form":"уйду́т","tags":["future","plural","third-person"]}]}""")),
            ("идут", Hit("идти", "meaning", """{"forms":[{"form":"иду́т","tags":["present","plural","third-person"]}]}""")));
        var future = MusicRhymeWordEvidence.Evaluate("пойдут", "уйдут", "Они пойдут", dictionary);
        Assert.IsTrue(future.Compatible); Assert.IsTrue(future.GrammarKnown);
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("пойдут", "идут", "Они пойдут", dictionary).Compatible);
    }

    [TestMethod]
    public void FullAndShortAdjectiveFormsAreNotInterchangeable()
    {
        var dictionary = Dictionary(
            ("тихий", Hit("тихий", "forms", """{"forms":[{"form":"ти́хий","tags":["decl_m_nom"]}]}""")),
            ("негромок", Hit("негромкий", "meaning", """{"forms":[{"form":"негро́мок","tags":["singular","masculine","short-form"]}]}""")));
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("тихий", "негромок", "Тихий голос", dictionary).Compatible);
    }

    [TestMethod]
    public void IncompleteFormsNeverClaimCheckedGrammar()
    {
        var dictionary = Dictionary(
            ("пыль", Hit("пыль", "forms", """{"gender":"f","forms":[{"form":"пы́ль","tags":["sg_nom"]}]}""")),
            ("праху", Hit("прах", "meaning", """{"forms":[{"form":"пра́ху","tags":["singular"]}]}""")));
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("пыль", "праху", "Пыль летала", dictionary).GrammarKnown);
    }

    [TestMethod]
    public void RestrictedRegistersDoNotPromoteObsoleteOrHistoricalSensesIntoEverydaySynonyms()
    {
        var dictionary = Dictionary(
            ("тумане", Hit("туман", "meaning", """{"senses":[{"glosses":["атмосферное явление из капель воды"]}],"synonyms":["туманность"]}""")),
            ("тумане", Hit("туман", "meaning", """{"senses":[{"glosses":["истор. нумизм. денежная единица Персии"]}],"synonyms":["томан"]}""")),
            ("томане", Hit("томан", "meaning", """{"senses":[{"glosses":["денежная единица Персии"]}],"forms":[{"form":"тома́не","tags":["singular","prepositional"]}]}""")),
            ("архаизм", Hit("архаизм", "meaning", """{"senses":[{"glosses":["старый синоним"],"tags":["obsolete"]}]}""")));
        Assert.AreEqual(0, MusicRhymeWordEvidence.Evaluate("тумане", "томане", "Маяк в тумане", dictionary).Meaning);
        Assert.IsFalse(MusicRhymeWordEvidence.Evaluate("тумане", "архаизм", "Маяк в тумане", dictionary).Compatible);
    }

    [TestMethod]
    public void MatchingOnlyTheImperativeBranchOfAnAmbiguousSourceDoesNotProveSentenceGrammar()
    {
        // The actual dictionary has both уйти/past plural and услать/imperative singular for ушли.
        var dictionary = Dictionary(
            ("ушли", Hit("уйти", "forms", """{"aspect":"perfective","forms":[{"form":"ушли́","tags":["past_pl"]}]}""")),
            ("ушли", Hit("услать", "forms", """{"aspect":"perfective","forms":[{"form":"ушли́","tags":["imperative_sg"]}]}""")),
            ("уволься", Hit("уволиться", "forms", """{"aspect":"perfective","forms":[{"form":"уво́лься","tags":["imperative_sg"]}]}""")));
        var fit = MusicRhymeWordEvidence.Evaluate("ушли", "уволься", "В нём танцуют призраки, что не ушли", dictionary);
        Assert.IsFalse(fit.GrammarKnown);
        Assert.IsTrue(fit.Grammar < 1);
    }

    private static RagLexiconHit Hit(string word, string kind, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(word, kind, document.RootElement.Clone(), false, "fixture", "CC0", "test fixture", word);
    }
    private static Func<string, IReadOnlyList<RagLexiconHit>> Dictionary(params (string Surface, RagLexiconHit Hit)[] items)
    {
        var map = items.GroupBy(i => i.Surface).ToDictionary(g => g.Key, g => (IReadOnlyList<RagLexiconHit>)g.Select(i => i.Hit).ToArray());
        return word => map.GetValueOrDefault(RagLexiconReader.Normalize(word), []);
    }
}
