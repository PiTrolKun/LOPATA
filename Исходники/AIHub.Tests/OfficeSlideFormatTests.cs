using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace AIHub.Tests;

[TestClass]
public class OfficeSlideFormatTests
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace T = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public void NativeDraftIsOrderedEditableAndReopensWithFormatting(string extension)
    {
        var slides = new List<OfficeSheetSection> { new() { Title = "Первый", Body = "Пункт 1\nПункт 2" }, new() { Title = "Second", Body = "End" } };
        var raw = OfficeNativeDocument.Create(extension, slides);
        if (extension == ".pptx")
        {
            using var stream = new MemoryStream(raw);
            using var doc = PresentationDocument.Open(stream, false);
            var errors = new OpenXmlValidator().Validate(doc).Take(3).ToArray();
            Assert.IsEmpty(errors, string.Join(", ",errors.Select(e => e.Description)));
        }
        else
        {
            using var zip = new ZipArchive(new MemoryStream(raw));
            Assert.AreEqual("mimetype", zip.Entries[0].FullName);
            Assert.AreEqual(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
        }
        var material = OfficeNativeDocument.Open(raw, extension);
        CollectionAssert.AreEqual(raw, material.Save());
        Assert.AreEqual(2, material.Pages.Count);
        Assert.AreEqual("Первый", material.Pages[0].Title);
        var block = material.Pages[1].Blocks.Single(b => b.Text == "End");
        const string value = "  Новое\tслово\nNext  line ";
        block.SetText(value); block.SetBold(true); block.SetItalic(true); block.SetFontSize(28.5);
        var output = material.Save();
        var restored = OfficeNativeDocument.Open(output,extension).Pages[1].Blocks.Single(b => b.Text == value);
        Assert.IsTrue(restored.Bold && restored.Italic);
        Assert.AreEqual(28.5, restored.FontSize);
        restored.SetBold(false); restored.SetItalic(false);
        Assert.IsFalse(restored.Bold || restored.Italic);
        Assert.AreEqual(28.5, restored.FontSize);
    }

    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public void AuthoredRunsAndInlineLinksKeepTheirTextBoundaries(string extension)
    {
        var original = OfficeNativeDocument.Create(extension,[new() { Title = "Title", Body = "Target" }]);
        var package = new OfficeNativePackage(original, false);
        var part = extension == ".pptx" ? "ppt/slides/slide1.xml" : "content.xml";
        var xml = package.Xml(part);
        if (extension == ".pptx")
        {
            var run = xml.Descendants(A + "r").Single(r => r.Element(A+"t")?.Value == "Target");
            run.AddAfterSelf(new XElement(A+"r",new XElement(A+"rPr",new XAttribute("i",1),
                new XElement(A+"hlinkClick",new XAttribute("action","ppaction://hlinkshowjump?jump=nextslide"))),new XElement(A+"t"," linked")));
        }
        else
        {
            var p = xml.Descendants(T+"p").Single(e => e.Value == "Target");
            p.Add(new XElement(T+"a",new XAttribute(XName.Get("href","http://www.w3.org/1999/xlink"),"#target")," linked"));
        }
        package.EndRead();
        var material = OfficeNativeDocument.Open(package.Snapshot()(),extension);
        var block = material.Pages[0].Blocks.Single(b => b.Text == "Target linked");
        block.SetText("Правка linked");
        var check = new OfficeNativePackage(material.Save(),false);
        try
        {
            var parsed = check.Xml(part);
            if(extension == ".pptx")
            {
                var link = parsed.Descendants(A+"hlinkClick").Single();
                Assert.AreEqual(" linked", link.Parent!.Parent!.Element(A+"t")!.Value);
                Assert.AreEqual("1",(string?)link.Parent.Attribute("i"));
            }
            else
            {
                var link = parsed.Descendants(T+"a").Single();
                Assert.AreEqual(" linked",link.Value);
                Assert.AreEqual("#target",link.Attributes().Single(a=>a.Name.LocalName=="href").Value);
            }
        }
        finally { check.EndRead(); }
    }

    [TestMethod, DataRow(".pptx"), DataRow(".odp")]
    public async Task AppendingRetainsExistingSlidesAndAddsEditableNativeText(string extension)
    {
        var source = OfficeNativeDocument.Create(extension, [new() { Title="Существующий", Body="Исходный текст" }]);
        var initial = OfficeNativeDocument.Open(source, extension);
        Assert.IsTrue(initial.CanAppend);
        var next = await initial.AppendAsync("Добавленный слайд", CancellationToken.None);
        Assert.AreEqual(1,initial.Pages.Count);
        Assert.AreEqual(2,next.Pages.Count);
        Assert.AreEqual("Добавленный слайд",next.Pages[1].Title);
        next.Pages[1].Blocks.Last().SetText("Новый текст");
        var saved=next.Save();
        var restored=OfficeNativeDocument.Open(saved,extension);
        Assert.AreEqual("Новый текст",restored.Pages[1].Blocks.Last().Text);
        if (extension == ".pptx")
        {
            using var material = PresentationDocument.Open(new MemoryStream(saved),false);
            var errors = new OpenXmlValidator().Validate(material).Take(3).ToArray();
            Assert.IsEmpty(errors,string.Join(", ",errors.Select(e=>e.Description)));
        }
    }

    [TestMethod]
    public void PasswordToModifyPresentationRemainsIntact()
    {
        var package = new OfficeNativePackage(OfficeNativeDocument.Create(".pptx", [new() { Title="Protected" }]),false);
        var presentation = package.Xml("ppt/presentation.xml").Root!;
        presentation.Add(new XElement(presentation.Name.Namespace + "modifyVerifier"));
        package.EndRead();
        var bytes = package.Snapshot()();
        var doc = OfficeNativeDocument.Open(bytes,".pptx");
        Assert.IsTrue(doc.ReadOnly);
        Assert.IsFalse(doc.CanAppend);
        CollectionAssert.AreEqual(bytes,doc.Save());
    }

    [TestMethod, DataRow(".pptx","RU"), DataRow(".pptx","EN"), DataRow(".odp","RU"), DataRow(".odp","EN")]
    public void SuppliedDeckPreservesMastersChartsPicturesAndOtherSlides(string extension,string language)
    {
        var path = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107", "user-suite", "AI_Office_TestSuite_RU_EN",
            "04_presentations", language, "complex_8_slides" + extension);
        if (!File.Exists(path)) Assert.Inconclusive("Install the private test corpus to verify the supplied presentation.");
        var original = File.ReadAllBytes(path);
        var material = OfficeNativeDocument.Open(original,extension);
        Assert.AreEqual(8,material.Pages.Count);
        CollectionAssert.AreEqual(original,material.Save());
        var first = material.Pages[0].Blocks.First(b=>b.Text.Length != 0);
        var replacement = first.Text + " · LOPATA";
        first.SetText(replacement); first.SetItalic(true);
        var result = material.Save();
        var restored = OfficeNativeDocument.Open(result,extension);
        Assert.AreEqual(8,restored.Pages.Count);
        Assert.IsTrue(restored.Pages[0].Blocks.Single(b=>b.Text==replacement).Italic);
        using var src = new ZipArchive(new MemoryStream(original));
        using var dst = new ZipArchive(new MemoryStream(result));
        CollectionAssert.AreEquivalent(src.Entries.Select(e=>e.FullName).ToArray(),dst.Entries.Select(e=>e.FullName).ToArray());
        var permitted = extension == ".pptx" ? "ppt/slides/slide1.xml" : "content.xml";
        foreach (var entry in src.Entries.Where(e=>e.FullName!=permitted))
        {
            using var before=entry.Open(); using var after=dst.GetEntry(entry.FullName)!.Open();
            CollectionAssert.AreEqual(SHA256.HashData(before),SHA256.HashData(after),entry.FullName);
        }
    }
}
