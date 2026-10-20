using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass]
public sealed class BookSmartSafetyTests
{
    [TestMethod]
    public void DigitalPdfUsesOnlyOriginalWordsAndRetainsUnassignedFootnote()
    {
        var content = new BookContent();
        content.ExactPageWords[0] = [new("Точный", .1, .1, .2, .2), new("текст", .3, .1, .4, .2), new("Сноска", .1, .9, .2, .95)];
        var page = new BookSmartPage([new(0, "Invented OCR replacement", Bounds: [0, 0, 1, .8]),
            new(1, "Duplicate fake text", Bounds: [0, 0, 1, .8])], []);
        var result = BookSmartPdfText.Preserve(content, 0, page);
        CollectionAssert.AreEqual(new[] { "Точный текст", "Сноска" }, result.Blocks.Select(block => block.Text).ToArray());
        Assert.IsTrue(result.Blocks[1].Questionable);
        Assert.IsFalse(result.Blocks.Any(block => block.Text.Contains("Invented", StringComparison.Ordinal)));
    }
    [TestMethod]
    public void AmbiguousTargetTextDoesNotReceiveAWarningFromAnotherOccurrence()
    {
        var content = new BookContent(); content.Parts.Add(new("", "<p>Повтор</p><p>Уникальный</p><p>Повтор</p>"));
        var target = BookReviewDocument.FromContent(content);
        var bound = BookSmartReviewBinding.Bind(target, [new("Повтор", BookReviewMarkKind.Questionable, "source"),
            new("Уникальный", BookReviewMarkKind.AiProcessed, "source")]);
        Assert.AreEqual(int.MaxValue, bound[0].Occurrence); Assert.AreEqual(0, bound[1].Occurrence);
    }
    [TestMethod]
    public void UnreadableOcrAndEmptyIllustrationAreExplicitAndRetained()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"schemaVersion":1,"status":"ok","issues":[],"blocks":[
              {"id":0,"text":"","kind":"image","questionable":false},
              {"id":1,"text":"","kind":"text","questionable":true}]}
            """);
        var page = BookSmartOcrWorker.Parse(json.RootElement);
        Assert.AreEqual(2, page.Blocks.Length); Assert.AreEqual("image", page.Blocks[0].Kind);
        Assert.AreEqual("⟦…⟧", page.Blocks[1].Text); Assert.IsTrue(page.Blocks[1].Questionable);
    }
}
