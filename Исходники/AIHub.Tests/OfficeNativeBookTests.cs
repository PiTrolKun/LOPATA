using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AIHub.Services.OfficeEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeNativeBookTests
{
    private static readonly XNamespace Fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    [TestMethod, DataRow(".fb2"), DataRow(".epub")]
    public void DraftChaptersRoundTripInTheirNativeFormat(string extension)
    {
        var bytes = OfficeNativeDocument.Create(extension, [
            new() { Title = "Первая", Body = "Абзац 1\nАбзац 2" },
            new() { Title = "Second", Body = "Unicode — текст" }]);
        var book = OfficeNativeDocument.Open(bytes,extension);
        CollectionAssert.AreEqual(new[] {"Первая","Second"},book.Pages.Select(p => p.Title).ToArray());
        CollectionAssert.AreEqual(bytes,book.Save());
        var last = book.Pages[1].Blocks.Last();
        last.SetText("Unicode — текст\nновый абзац");
        last.SetText("Unicode — текст\nисправленный абзац"); // A second keystroke on the existing block.
        Assert.AreEqual("Unicode — текст\nисправленный абзац",last.Text);
        var snapshot = book.Save();
        var reopened = OfficeNativeDocument.Open(snapshot,extension);
        var paragraphs = reopened.Pages[1].Blocks.Select(b => b.Text).ToList();
        CollectionAssert.Contains(paragraphs,"Unicode — текст");
        CollectionAssert.Contains(paragraphs,"исправленный абзац");
        if (extension == ".epub")
        {
            Assert.AreEqual("mimetype",Encoding.ASCII.GetString(snapshot,30,8));
            Assert.AreEqual(0,BitConverter.ToUInt16(snapshot,8));
            using var zip = new ZipArchive(new MemoryStream(snapshot));
            Assert.AreEqual(1,Xml(zip,"EPUB/chapter-2.xhtml").Descendants(Html + "br").Count());
        }
        WriteEvidence("created"+extension,snapshot);
    }

    [TestMethod]
    public void NestedFb2SectionsKeepInlineLinksBinaryAndNotesAcrossLineBreaks()
    {
        var xml = """
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
            <description/><body><section id="parent"><title><p>Глава</p></title>
            <section id="child"><title><p>Подглава</p></title>
            <p id="keep"><strong>Жирно</strong> <a id="anchor" l:href="#note">ссылка</a></p></section></section></body>
            <body name="notes"><section id="note"><title><p>Примечание</p></title><p>Примечание в исходнике</p></section></body>
            <binary id="original" content-type="image/png">AQIDBA==</binary></FictionBook>
            """;
        var original = Encoding.UTF8.GetBytes(xml);
        var book = OfficeNativeDocument.Open(original,".fb2");
        CollectionAssert.AreEqual(new[] {"Глава","Подглава","Примечание"},book.Pages.Select(p => p.Title).ToArray());
        var text = book.Pages[1].Blocks[1];
        text.SetText("Жирно ссы\nлка");
        text.SetText("Жирно ссы\nлка!");
        var split = XDocument.Load(new MemoryStream(book.Save()));
        var parts = split.Descendants(Fb + "section").Single(s => (string?)s.Attribute("id") == "child").Elements(Fb + "p").ToArray();
        Assert.AreEqual(2,parts.Length);
        Assert.AreEqual("Жирно", parts[0].Element(Fb + "strong")!.Value);
        Assert.AreEqual("ссы",parts[0].Element(Fb + "a")!.Value);
        Assert.AreEqual("лка!",parts[1].Element(Fb + "a")!.Value);
        Assert.AreEqual(1,split.Descendants().Attributes("id").Count(id => id.Value == "anchor"));
        Assert.AreEqual("#note",(string?)parts[1].Element(Fb + "a")!.Attribute(XName.Get("href","http://www.w3.org/1999/xlink")));
        Assert.AreEqual("AQIDBA==",split.Descendants(Fb + "binary").Single().Value);
        text.SetText("Жирно ссылку"); // Undoing the line break restores a single paragraph.
        var final = XDocument.Load(new MemoryStream(book.Save()));
        Assert.AreEqual(1,final.Descendants(Fb + "section").Single(s => (string?)s.Attribute("id") == "child").Elements(Fb + "p").Count());
    }

    [TestMethod]
    public void EpubSpineControlsReadingOrderAndTitleEditsUpdateNavigation()
    {
        var source = OfficeNativeDocument.Create(".epub",[
            new() {Title="Первый",Body="A"},new() {Title="Второй",Body="B"}]);
        var wanted = new[] {"Второй","Первый"};
        using var editable = new MemoryStream();
        editable.Write(source); editable.Position = 0;
        using (var zip = new ZipArchive(editable,ZipArchiveMode.Update,true))
        {
            var opf = Xml(zip,"EPUB/content.opf");
            var spine = opf.Descendants(Opf + "spine").Single();
            spine.ReplaceNodes(spine.Elements().Reverse().ToArray());
            zip.GetEntry("EPUB/content.opf")!.Delete();
            using var stream = zip.CreateEntry("EPUB/content.opf").Open();
            opf.Save(stream);
        }
        var book = OfficeNativeDocument.Open(editable.ToArray(),".epub");
        CollectionAssert.AreEqual(wanted,book.Pages.Select(s => s.Title).ToArray());
        book.Pages[0].Blocks[0].SetText("Второй обновлён");
        using var result = new ZipArchive(new MemoryStream(book.Save()));
        Assert.AreEqual("Второй обновлён",Xml(result,"EPUB/nav.xhtml").Descendants(Html + "a").Last().Value);
        Assert.AreEqual("Второй обновлён",Xml(result,"EPUB/chapter-2.xhtml").Descendants(Html + "title").Single().Value);
        using var before = new ZipArchive(new MemoryStream(editable.ToArray()));
        foreach (var name in new[] {"mimetype","META-INF/container.xml","EPUB/content.opf","EPUB/chapter-1.xhtml"})
            CollectionAssert.AreEqual(Bytes(before,name),Bytes(result,name),name);
    }

    [TestMethod]
    public void EpubChaptersInOneSpineDocumentFollowTocAnchors()
    {
        var source = OfficeNativeDocument.Create(".epub",[new() {Title = "Книга",Body = "Введение"}]);
        var package = new OfficeNativePackage(source,false);
        byte[] fixture;
        try
        {
            var article = package.Xml("EPUB/chapter-1.xhtml").Descendants(Html + "body").Single();
            var nav = package.Xml("EPUB/nav.xhtml").Descendants(Html + "ol").Single();
            foreach (var name in new[] {"a", "b"})
            {
                article.Add(new XElement(Html + "section",new XAttribute("id",name),new XElement(Html + "h2",name),
                    new XElement(Html + "p","Text"),new XElement(Html + "h3","Subheading")));
                nav.Add(new XElement(Html + "li",new XElement(Html + "a",new XAttribute("href","chapter-1.xhtml#"+name),name)));
            }
            fixture = package.Snapshot()();
        }
        finally { package.EndRead(); }
        var book = OfficeNativeDocument.Open(fixture,".epub");
        CollectionAssert.AreEqual(new[] {"Книга","a","b"},book.Pages.Select(page => page.Title).ToArray());
        book.Pages[2].Blocks[0].SetText("changed");
        using var saved = new ZipArchive(new MemoryStream(book.Save()));
        Assert.AreEqual("changed", Xml(saved,"EPUB/nav.xhtml").Descendants(Html + "a").Last().Value);
    }

    [TestMethod, DataRow("epub2_chapters_footnotes.epub"),DataRow("epub3_chapters_image.epub"),DataRow("fb2_multichapter_image_notes.fb2")]
    public async Task LocalReaderCorpusKeepsBookStructure(string filename)
    {
        var corpus = Environment.GetEnvironmentVariable("LOPATA_NATIVE_BOOK_CORPUS");
        if (string.IsNullOrEmpty(corpus)) Assert.Inconclusive("Set LOPATA_NATIVE_BOOK_CORPUS to the RU ebook fixture directory.");
        var source = File.ReadAllBytes(Path.Combine(corpus,filename));
        var ext = Path.GetExtension(filename);
        var book = OfficeNativeDocument.Open(source,ext);
        var chapter = book.Pages.Single(page => page.Title == "Глава 4");
        var heading = chapter.Blocks.First();
        heading.SetText("Глава 4 — проверка");
        var text = chapter.Blocks.First(b => b.Text.StartsWith("Сноски",StringComparison.Ordinal));
        text.SetText(text.Text.Replace("Сноски","Проверены сноски",StringComparison.Ordinal));
        if (ext == ".epub")
        {
            var mixed = chapter.Blocks.Single(b => b.Text.Contains("external link"));
            mixed.SetText(mixed.Text.Replace("Bold","Жирный",StringComparison.Ordinal));
        }
        var result = book.Save();
        var saved = OfficeNativeDocument.Open(result,ext);
        var check = saved.Pages.Single(s => s.Title == "Глава 4 — проверка");
        Assert.IsTrue(check.Blocks.Any(b => b.Text.StartsWith("Проверены сноски",StringComparison.Ordinal)));
        if (ext == ".epub")
        {
            using var old = new ZipArchive(new MemoryStream(source));
            using var fresh = new ZipArchive(new MemoryStream(result));
            foreach (var e in old.Entries.Where(e => e.FullName is not ("EPUB/text/ch004.xhtml" or "EPUB/nav.xhtml" or "EPUB/toc.ncx")))
                CollectionAssert.AreEqual(Bytes(old,e.FullName),Bytes(fresh,e.FullName),e.FullName);
            var content = Xml(fresh,"EPUB/text/ch004.xhtml");
            Assert.AreEqual("Жирный",content.Descendants(Html + "strong").Single().Value);
            Assert.AreEqual("italic",content.Descendants(Html + "em").Single().Value);
            Assert.AreEqual("https://example.org",(string?)content.Descendants(Html + "a").Single().Attribute("href"));
            Assert.IsTrue(Xml(fresh,"EPUB/toc.ncx").Descendants().Any(t => t.Name.LocalName == "text" && t.Value == "Глава 4 — проверка"));
        }
        else
        {
            var old = XDocument.Load(new MemoryStream(source),LoadOptions.PreserveWhitespace);
            var fresh = XDocument.Load(new MemoryStream(result),LoadOptions.PreserveWhitespace);
            foreach (var tag in new[] {"description","binary"})
                Assert.IsTrue(XNode.DeepEquals(old.Root!.Element(Fb+tag),fresh.Root!.Element(Fb+tag)),tag);
            Assert.IsTrue(XNode.DeepEquals(old.Root!.Elements(Fb+"body").Last(),fresh.Root!.Elements(Fb+"body").Last()));
        }
        WriteEvidence(filename,result);
        Assert.IsTrue(saved.CanAppend);
        var added = await saved.AppendAsync("Дополнительная глава", CancellationToken.None);
        var newChapter = added.Pages.Single(s => s.Title == "Дополнительная глава");
        Assert.AreEqual(2,newChapter.Blocks.Count);
        newChapter.Blocks[1].SetText("Содержимое добавленной главы");
        var grown = added.Save();
        var again = OfficeNativeDocument.Open(grown,ext);
        Assert.AreEqual(check.Blocks.Count,again.Pages.Single(p => p.Title == "Глава 4 — проверка").Blocks.Count);
        Assert.AreEqual("Содержимое добавленной главы",again.Pages.Single(p => p.Title == "Дополнительная глава").Blocks[1].Text);
        if (ext == ".epub")
        {
            using var zip = new ZipArchive(new MemoryStream(grown));
            using var previous = new ZipArchive(new MemoryStream(result));
            var nav = Xml(zip,"EPUB/nav.xhtml");
            Assert.AreEqual("Дополнительная глава",nav.Descendants(Html + "a").Single(a => a.Value == "Дополнительная глава").Value);
            var link = (string)nav.Descendants(Html + "a").Single(a => a.Value == "Дополнительная глава").Attribute("href")!;
            var ncx = Xml(zip,"EPUB/toc.ncx");
            var lastPoint = ncx.Descendants().Last(x => x.Name.LocalName == "navPoint");
            Assert.AreEqual(link,(string?)lastPoint.Elements().Single(e => e.Name.LocalName == "content").Attribute("src"));
            Assert.AreEqual("Дополнительная глава",again.Pages[^1].Title);
            foreach (var entry in previous.Entries.Where(e => e.FullName is not ("EPUB/nav.xhtml" or "EPUB/toc.ncx" or "EPUB/content.opf")))
                CollectionAssert.AreEqual(Bytes(previous,entry.FullName),Bytes(zip,entry.FullName),entry.FullName);
        }
        else
        {
            var doc = XDocument.Load(new MemoryStream(grown));
            Assert.AreEqual("Дополнительная глава",doc.Root!.Elements(Fb + "body").First().Elements(Fb + "section").Last().Element(Fb + "title")!.Value);
            Assert.AreEqual("notes",(string?)doc.Root.Elements(Fb+"body").Last().Attribute("name"));
        }
        WriteEvidence("appended-"+filename,grown);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public void EpubDistinguishesFontObfuscationFromEncryptedChapters(bool font)
    {
        var bytes = OfficeNativeDocument.Create(".epub",[new() { Title = "Глава", Body = "Текст" }]);
        using var expanded = new MemoryStream();
        expanded.Write(bytes); expanded.Position = 0;
        using (var zip = new ZipArchive(expanded,ZipArchiveMode.Update,true))
        {
            XNamespace cipher = "http://www.w3.org/2001/04/xmlenc#";
            var resource = font ? "EPUB/fonts/embedded.otf" : "EPUB/chapter-1.xhtml";
            var encrypted = new XElement("encryption", new XElement(cipher + "EncryptedData",
                new XElement(cipher + "EncryptionMethod",new XAttribute("Algorithm","http://www.idpf.org/2008/embedding")),
                new XElement(cipher + "CipherData",new XElement(cipher + "CipherReference",new XAttribute("URI",resource)))));
            using var output = zip.CreateEntry("META-INF/encryption.xml").Open();
            encrypted.Save(output);
        }
        var source = expanded.ToArray(); var book = OfficeNativeDocument.Open(source,".epub");
        Assert.AreEqual(!font,book.ReadOnly);
        Assert.AreEqual(font,book.CanAppend);
        CollectionAssert.AreEqual(source,book.Save());
    }

    private static void WriteEvidence(string name,byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_NATIVE_BOOK_EVIDENCE") is not { Length: > 0 } outDir) return;
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir,name),bytes);
    }

    private static byte[] Bytes(ZipArchive zip,string name)
    {
        using var input = zip.GetEntry(name)!.Open(); using var data = new MemoryStream();
        input.CopyTo(data); return data.ToArray();
    }
    private static XDocument Xml(ZipArchive zip,string name)
    {
        using var input = zip.GetEntry(name)!.Open();
        return XDocument.Load(input);
    }
}
