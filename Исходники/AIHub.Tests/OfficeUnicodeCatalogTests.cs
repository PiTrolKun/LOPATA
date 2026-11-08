using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeUnicodeCatalogTests
{
    [TestMethod]
    public async Task CompleteRepertoireHasNamesBlocksAndCompositeEmoji()
    {
        var catalog = await OfficeUnicodeCatalog.LoadAsync(default);
        Assert.AreEqual("18.0.0", catalog.Version);
        Assert.IsTrue(catalog.Symbols.Length > 170000);
        Assert.IsTrue(catalog.Blocks.Length > 350);
        Assert.IsTrue(catalog.EmojiCount > 3900);
        Assert.AreEqual("HANGUL SYLLABLE GA", catalog.Search("U+AC00", OfficeSymbolScope.All, -1, default).Single().Name);
        Assert.AreEqual("CJK UNIFIED IDEOGRAPH-4E00", catalog.Search("U+4E00", OfficeSymbolScope.All, -1, default).Single().Name);
        Assert.AreEqual("👨‍👩‍👧‍👦", catalog.Search("U+1F468 U+200D U+1F469 U+200D U+1F467 U+200D U+1F466", OfficeSymbolScope.All, -1, default).Single().Text);
        Assert.IsTrue(catalog.Search("сердце", OfficeSymbolScope.Emoji, -1, default).Any(s => s.Text == "❤️"));
        Assert.IsTrue(catalog.Search("arrow", OfficeSymbolScope.Arrows, -1, default).Length > 100);
        Assert.AreEqual(0, catalog.Search("U+110000", OfficeSymbolScope.All, -1, default).Length);
    }

    [TestMethod, DataRow("солнце"), DataRow("Sun"), DataRow("Сол")]
    public async Task TypingWordsSuggestSunInBothLanguages(string word)
    {
        var catalog = await OfficeUnicodeCatalog.LoadAsync(default);
        var answers = catalog.Suggestions.Find(word);
        Assert.AreEqual("☀️", answers[0].Text);
        Assert.IsTrue(answers.Any(s => s.Name.Contains("SUN", StringComparison.OrdinalIgnoreCase) || s.Keywords.Contains("sun", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(answers.Length <= 8);
    }

    [TestMethod, DataRow("рубль", "₽"), DataRow("ruble", "₽"), DataRow("RUB", "₽"), DataRow("доллар", "$"), DataRow("dollar", "$"), DataRow("евро", "€"), DataRow("euro", "€")]
    public async Task CurrencyNamesAndIsoCodesSuggestTheirGlyphs(string word, string symbol)
    {
        var catalog = await OfficeUnicodeCatalog.LoadAsync(default);
        Assert.IsTrue(catalog.Suggestions.Find(word).Any(s => s.Text == symbol));
    }

    [TestMethod]
    public async Task EmoticonsAndTypedTokensHaveExplicitBoundaries()
    {
        var catalog = await OfficeUnicodeCatalog.LoadAsync(default);
        Assert.IsTrue(catalog.Suggestions.Find("=)").Any(s => s.Text == "😀"));
        Assert.IsTrue(catalog.Suggestions.Find("<3").Any(s => s.Text == "❤️"));
        Assert.AreEqual(new OfficeSymbolToken(7, 6, "Солнце"), OfficeSymbolSuggestions.TokenAt("Привет Солнце", 13));
        Assert.AreEqual(new OfficeSymbolToken(0, 2, "=)"), OfficeSymbolSuggestions.TokenAt("=)", 2));
        Assert.IsNull(OfficeSymbolSuggestions.TokenAt("https://sun", 11));
        Assert.IsNull(OfficeSymbolSuggestions.TokenAt("sun ", 4));
    }
}
