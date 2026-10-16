using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicRhymeSyntaxTests
{
    [TestMethod]
    public void APrepositionAndOneOrTwoAdjectivesProtectTheirNounHead()
    {
        Assert.IsFalse(Check("Мы шли по тёмной тропе.", "тропе"));
        Assert.IsFalse(Check("Мы шли по узкой тёмной тропе.", "тропе"));
        Assert.IsFalse(Check("Птица скрылась под широкой аркой.", "аркой"));
        Assert.IsTrue(Check("Тёмная тропа петляла.", "тропа"));
    }

    [TestMethod]
    public void ADetachedAdjectiveDoesNotInheritAnAssumedMeaningFromADistantNoun()
    {
        Assert.IsFalse(Check("День угас, бесшумный, спокойный.", "спокойный"));
        Assert.IsFalse(Check("Вечер наступил, спокойный.", "спокойный"));
        Assert.IsTrue(Check("Спокойный вечер наступил.", "Спокойный"));
        Assert.IsTrue(Check("Наступил вечер спокойный.", "спокойный"));
    }

    [TestMethod]
    public void EvidenceForSomeOtherFormCannotCreateAnAdjectiveAttachment()
    {
        Assert.IsTrue(Check("День угас, спокойным.", "спокойным"));
        Assert.IsTrue(Check("Мы шли по неизвестной тропе.", "тропе", _ => []));
    }

    [TestMethod]
    public void OnlyTheLocalFourWordWindowIsLookedUp()
    {
        var seen = new List<string>();
        Assert.IsFalse(Check(string.Concat(Enumerable.Repeat("далеко ", 20_000)) + "Мы шли по тёмной тропе.", "тропе",
            word => { seen.Add(word); return Lookup(word); }));
        Assert.IsLessThanOrEqualTo(4, seen.Count); Assert.IsFalse(seen.Contains("далеко"));
    }

    [TestMethod]
    public void InvalidOrPartialWordRangesAreRejected()
    {
        var line = Line("Спокойный вечер.");
        Assert.IsFalse(MusicRhymeSyntax.CanReplace(line, -1, 1, Lookup));
        Assert.IsFalse(MusicRhymeSyntax.CanReplace(line, 0, 3, Lookup));
    }

    [TestMethod]
    public void ActualAccentAndSurnameSchemaCannotHideTheOrdinaryAdjective()
    {
        // Reduced exact records from installed OpenRussian + Kaikki Lookup; the third hit is a surname.
        RagLexiconHit Hit(string word, string kind, string json) => new(word, kind,
            JsonSerializer.Deserialize<JsonElement>(json), false, "fixture", "CC0", "actual schema mirror", word);
        IReadOnlyList<RagLexiconHit> Actual(string word) => word switch {
            "серебряной" => [
                Hit("серебряный", "forms", """{"variants":["сере́бряный"],"translations":["silver"],"forms":[{"form":"сере́бряной","tags":["decl_f_gen"]},{"form":"сере́бряной","tags":["decl_f_dat"]},{"form":"сере́бряной","tags":["decl_f_inst"]},{"form":"сере́бряной","tags":["decl_f_prep"]}]}"""),
                Hit("серебряный", "meaning", """{"senses":[],"forms":[{"form":"сере́бряной","tags":["singular","feminine","dative"]}]}"""),
                Hit("Серебряная", "meaning", """{"senses":[],"forms":[{"form":"Сере́бряной","tags":["singular","dative"]}]}""")],
            "дороге" => [
                Hit("дорога", "forms", """{"gender":"f","animate":"0","indeclinable":"0","forms":[{"form":"доро́ге","tags":["sg_dat"]},{"form":"доро́ге","tags":["sg_prep"]}]}"""),
                Hit("дорога", "meaning", """{"senses":[],"forms":[{"form":"доро́ге","tags":["singular","dative"]},{"form":"доро́ге","tags":["singular","prepositional"]}]}""")], _ => [] };
        Assert.IsFalse(Check("Мы идём по серебряной дороге.", "дороге", Actual));
        Assert.IsTrue(Check("Мы идём по серебряной дороге.", "серебряной", Actual));
    }

    private static bool Check(string text, string word, Func<string, IReadOnlyList<RagLexiconHit>>? lookup = null) =>
        MusicRhymeSyntax.CanReplace(Line(text), text.IndexOf(word, StringComparison.Ordinal), word.Length, lookup ?? Lookup);
    private static MusicRhymeLine Line(string text) => new(1, 0, text.Length, 0, text, "", 0, "ru", null, []);
    private static IReadOnlyList<RagLexiconHit> Lookup(string word)
    {
        var tag = word switch { "тёмной" or "узкой" => "decl_f_dat", "тропе" => "sg_dat", "тропа" => "sg_nom",
            "широкой" => "singular feminine instrumental", "аркой" => "singular instrumental",
            "спокойный" or "бесшумный" => "decl_m_nom", "вечер" => "sg_nom", _ => "" };
        if (tag.Length == 0) return [];
        var data = JsonSerializer.SerializeToElement(new { forms = new[] { new { form = word, tags = tag.Split(' ') } } });
        return [new(word, "forms", data, false, "fixture", "CC0", "test fixture", word)];
    }
}
