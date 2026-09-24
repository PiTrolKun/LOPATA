using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportQuickPreviewTests
{
    [TestMethod]
    public void FindsSeparateWorksInsideOneDialogAndSameWorkAcrossDialogs()
    {
        var input = new ImportInput(
            [new("a", "Общий диалог", 4), new("b", "Продолжение", 2)],
            [
                Unit("1", "a", "m1", "Название книги: Легенда о Падшем Боге"),
                Unit("2", "a", "m2", new string('А', 410)),
                Unit("3", "a", "m3", "Название книги: Тихая гавань"),
                Unit("4", "a", "m4", new string('Б', 210)),
                Unit("5", "b", "m5", "Книга: Легенда о Падшем Боге"),
                Unit("6", "b", "m6", "Обычный разговор")
            ], [], []);
        var groups = ImportQuickPreview.Scan(input, ["a", "b"], "Легенда о Падшем Боге");
        Assert.AreEqual(3, groups.Count);
        Assert.AreEqual("Легенда о Падшем Боге", groups[0].Name);
        Assert.AreEqual("Тихая гавань", groups[1].Name);
        Assert.IsTrue(groups[^1].IsOther);
        var assigned = groups.SelectMany(g => g.Variants).SelectMany(v => v.Parts)
            .SelectMany(p => p.UnitIds).ToArray();
        CollectionAssert.AreEquivalent(input.Units.Select(u => u.Id).ToArray(), assigned);
        Assert.AreEqual(2, groups[0].Variants.SelectMany(v => v.Parts)
            .Select(p => p.ConversationId).Distinct().Count());
    }

    [TestMethod]
    public void SimilarSpellingIsOnlyAProvisionalVariantAndTechnicalTextIsExcluded()
    {
        var input = new ImportInput([new("a", "Диалог", 4)],
            [Unit("1", "a", "m1", "Название книги: Поиск понимания в собственных чувствах"),
             Unit("2", "a", "m2", "Название книги: Поиск понимания в собственные чувствах"),
             Unit("3", "a", "m3", "Обычная реплика"),
             new("4", "a", "m4", "", "THINK", 0, 0, "Название книги: Ложная книга", true)], [], []);
        var groups = ImportQuickPreview.Scan(input, ["a"], "Поиск понимания в собственных чувствах");
        Assert.AreEqual(2, groups.Count);
        Assert.AreEqual(2, groups[0].Variants.Count);
        Assert.IsTrue(groups[^1].IsOther);
        Assert.IsFalse(groups.SelectMany(g => g.Variants).SelectMany(v => v.Parts)
            .SelectMany(p => p.UnitIds).Contains("4"));
    }

    [TestMethod]
    public void FindsBookNamedInsideQuotedConversationWithoutLabel()
    {
        var input = new ImportInput([new("a", "Разговор", 3)],
            [Unit("1", "a", "m1", "Давай начнём «Легенда о Падшем Боге». Главы подойдут."),
             Unit("2", "a", "m2", new string('А', 250)),
             Unit("3", "a", "m3", "Короткий ответ")], [], []);
        var groups = ImportQuickPreview.Scan(input, ["a"], "Другое название");
        Assert.AreEqual("Легенда о Падшем Боге", groups[0].Name);
        Assert.IsTrue(groups[^1].IsOther);
        Assert.IsTrue(groups[0].Variants.SelectMany(v => v.Parts)
            .SelectMany(p => p.UnitIds).Contains("2"));
    }

    private static ImportUnit Unit(string id, string conversation, string message, string text) =>
        new(id, conversation, message, "", "RESPONSE", 0, 0, text, false);
}
