using System.IO;
using System.Xml.Linq;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeNativeDocumentTests
{
    [TestMethod]
    public void ReplacementPreservesTrailingRunsAndUnicode()
    {
        var plain = new XText("до ");
        var link = new XElement("a", new XAttribute("href", "#note"), new XText("🙂ссылка"));
        var tail = new XText(" конец");
        var page = new XElement("p", plain, link, tail);
        OfficeNativeText.Replace([plain, (XText)link.FirstNode!, tail], "до 😁ссылка конец");
        Assert.AreEqual("😁ссылка", link.Value);
        Assert.AreEqual("#note", (string?)link.Attribute("href"));
        Assert.AreEqual("до 😁ссылка конец", page.Value);
    }

    [TestMethod]
    public async Task AutosaveAndAppendUseTheRevisionAtRequestTime()
    {
        var doc = OfficeNativeDocument.Open(OfficeNativeDocument.Create(".fb2",
            [new OfficeSheetSection { Title = "Глава", Body = "Черновик" }]), ".fb2");
        var paragraph = doc.Pages[0].Blocks.Single(b => b.Text == "Черновик");
        paragraph.SetText("Первый текст");
        var checkpoint = doc.SaveAsync(CancellationToken.None);
        var append = doc.AppendAsync("Новая глава", CancellationToken.None);
        paragraph.SetText("Следующая правка");
        var saved = OfficeNativeDocument.Open(await checkpoint, ".fb2");
        var withChapter = await append;
        Assert.IsTrue(saved.Pages[0].Blocks.Any(b => b.Text == "Первый текст"));
        Assert.AreEqual(1, saved.Pages.Count);
        Assert.AreEqual(2, withChapter.Pages.Count);
        Assert.IsTrue(withChapter.Pages[0].Blocks.Any(b => b.Text == "Первый текст"));
        Assert.IsTrue(doc.Pages[0].Blocks.Any(b => b.Text == "Следующая правка"));
    }

    [TestMethod]
    public void PackageLinksStayInsideThePublication()
    {
        Assert.AreEqual("EPUB/text one/ch1.xhtml", OfficeNativePackage.Resolve("EPUB/nav/contents.xhtml", "../text%20one/ch1.xhtml#top"));
        Assert.AreEqual("EPUB/a.xhtml", OfficeNativePackage.Resolve("EPUB/a.xhtml", "#bookmark"));
        foreach (var bad in new[] { "../../outside", "%2e%2e/%2e%2e/outside", "https://example.org/x", "//host/x", "a%5cb" })
            Assert.ThrowsExactly<InvalidDataException>(() => OfficeNativePackage.Resolve("EPUB/package.opf", bad));
    }
}
