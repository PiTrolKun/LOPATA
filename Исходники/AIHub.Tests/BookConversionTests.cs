using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AIHub.Services.Books;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class BookConversionTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "LOPATA-BookTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Source(string name, string text) { var path = Path.Combine(_root, name); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private Task<BookConversionResult> Convert(string[] source, string format, bool merge = false, IProgress<BookConversionProgress>? progress = null, CancellationToken ct = default) => new BookConversionService().ConvertAsync(new() { Sources = source, OutputFormat = format, OutputDirectory = Path.Combine(_root, "out"), Merge = merge }, progress, ct);
    private static string Success(BookConversionResult result)
    {
        Assert.AreEqual(0, result.Failures.Length, string.Join("; ", result.Failures.Select(f => f.Message)));
        Assert.AreEqual(1, result.Outputs.Length); Assert.IsFalse(result.Cancelled); return result.Outputs[0];
    }
    private static string ZipText(string path, string entry) { using var zip = ZipFile.OpenRead(path); using var reader = new StreamReader(zip.GetEntry(entry)!.Open()); return reader.ReadToEnd(); }

    [TestMethod]
    public async Task NativeReverseChainPreservesRussianTextAuthorAndSources()
    {
        var html = Source("Книга.html", "<html lang='ru'><head><title>Ночная книга</title><meta name='author' content='Автор Читатель'></head><body><h1>Первая глава</h1><p>Ёжик идёт к реке. 日本語.</p><p>Вторая строка.</p></body></html>");
        var original = SHA256.HashData(File.ReadAllBytes(html));
        var epub = Success(await Convert([html], "epub"));
        StringAssert.Contains(ZipText(epub, "OEBPS/content.opf"), "Автор Читатель");
        var fb2 = Success(await Convert([epub], "fb2"));
        var docx = Success(await Convert([fb2], "docx"));
        using (var package = WordprocessingDocument.Open(docx, false))
        {
            Assert.AreEqual("Автор Читатель", package.PackageProperties.Creator);
            var document = package.MainDocumentPart?.Document; Assert.IsNotNull(document);
            StringAssert.Contains(document.InnerText, "Ёжик идёт к реке. 日本語.");
            var issues = new OpenXmlValidator().Validate(package).Take(5).ToArray();
            Assert.AreEqual(0, issues.Length, string.Join("; ", issues.Select(x => x.Description)));
        }
        var txt = Success(await Convert([docx], "txt"));
        StringAssert.Contains(File.ReadAllText(txt), "Ёжик идёт к реке. 日本語.");
        CollectionAssert.AreEqual(original, SHA256.HashData(File.ReadAllBytes(html)));
    }
    [TestMethod]
    public async Task NewOutputsNeverOverwriteSourcesOrExistingResults()
    {
        var source = Source("Один.txt", "Содержимое один");
        var before = File.ReadAllBytes(source);
        var service = new BookConversionService();
        var request = new BookConversionRequest { Sources = [source], OutputFormat = "txt", OutputDirectory = _root };
        var results = await Task.WhenAll(service.ConvertAsync(request), service.ConvertAsync(request));
        var paths = results.Select(Success).ToArray(); Assert.AreNotEqual(paths[0], paths[1]);
        Assert.IsTrue(paths.All(p => !p.Equals(source, StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(source));
        Assert.IsTrue(paths.All(p => File.ReadAllText(p).Contains("LOPATA", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task OrderedMergeDisambiguatesAnchorsAndKeepsNotesAndImages()
    {
        var image = Path.Combine(_root, "pic.png");
        using (var bitmap = new SKBitmap(30, 20)) { bitmap.Erase(SKColors.Green); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var output = File.Create(image); data.SaveTo(output); }
        var first = Source("1.html", "<h1>Первый</h1><p id='same'>Первый текст <a href='#note'>сноска</a></p><p id='note'>Первое примечание</p><img src='pic.png'>");
        var second = Source("2.html", "<h1>Второй</h1><p id='same'>Второй текст <a href='#note'>сноска</a></p><p id='note'>Второе примечание</p><img src='pic.png'>");
        var epub = Success(await Convert([second, first], "epub", true));
        var html = ZipText(epub, "OEBPS/book.xhtml");
        Assert.IsTrue(html.IndexOf("Второй текст", StringComparison.Ordinal) < html.IndexOf("Первый текст", StringComparison.Ordinal));
        StringAssert.Contains(html, "b0_note"); StringAssert.Contains(html, "b1_note");
        var xml = XDocument.Parse(html); var ids = xml.Descendants().Attributes("id").Select(x => x.Value).ToArray(); Assert.AreEqual(ids.Length, ids.Distinct().Count());
        using var zip = ZipFile.OpenRead(epub); Assert.AreEqual(2, zip.Entries.Count(e => e.FullName.EndsWith(".png")));
    }
    [TestMethod]
    public async Task PartialFailureKeepsCompletedFilesAndSourceBytes()
    {
        var good = Source("Хорошая.txt", "Полезный текст"); var bad = Source("Сломанная.epub", "not zip");
        var result = await Convert([bad, good], "docx");
        Assert.AreEqual(1, result.Outputs.Length); Assert.AreEqual(1, result.Failures.Length); Assert.IsTrue(File.Exists(result.Outputs[0]));
        Assert.AreEqual("not zip", File.ReadAllText(bad)); Assert.AreEqual("Полезный текст", File.ReadAllText(good));
    }
    [TestMethod]
    public async Task CancellationKeepsOnlyAlreadyPublishedFiles()
    {
        var one = Source("1.txt", "Первая"); var two = Source("2.txt", "Вторая"); using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(p => { if (p.Phase == "completed") cancel.Cancel(); });
        var result = await Convert([one, two], "epub", progress: progress, ct: cancel.Token);
        Assert.IsTrue(result.Cancelled); Assert.AreEqual(1, result.Outputs.Length); Assert.IsTrue(File.Exists(result.Outputs[0]));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_root, "out")).Length);
    }
    [TestMethod]
    public async Task ImageToCbzWritesGraphicPagesAndMetadata()
    {
        var png = Path.Combine(_root, "page.png"); using (var bitmap = new SKBitmap(20, 30)) { bitmap.Erase(SKColors.Red); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var output = File.Create(png); data.SaveTo(output); }
        var result = Success(await Convert([png], "cbz")); using var zip = ZipFile.OpenRead(result);
        Assert.AreEqual(1, zip.Entries.Count(e => e.FullName.EndsWith(".jpg")));
        StringAssert.Contains(ZipText(result, "ComicInfo.xml"), "LOPATA");
    }
    [TestMethod]
    public async Task HtmlCannotReadImageOutsideItsOwnFolder()
    {
        var nested = Path.Combine(_root, "nested"); Directory.CreateDirectory(nested); File.WriteAllBytes(Path.Combine(_root, "secret.png"), [1, 2, 3]);
        var source = Path.Combine(nested, "unsafe.html"); File.WriteAllText(source, "<p>test</p><img src='../secret.png'>");
        var result = await Convert([source], "epub"); Assert.AreEqual(0, result.Outputs.Length); Assert.AreEqual(1, result.Failures.Length);
    }
    [TestMethod]
    public async Task UnsafeArchiveIsRejectedWithoutExtracting()
    {
        var path = Path.Combine(_root, "unsafe.epub"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("escape"); }
        var result = await Convert([path], "fb2"); Assert.AreEqual(0, result.Outputs.Length); Assert.AreEqual(1, result.Failures.Length); Assert.IsFalse(File.Exists(Path.Combine(_root, "escape.txt")));
    }
    [TestMethod]
    public async Task PdfIsReadableAndContainsSourceTextAndProvenance()
    {
        var input = Source("Файл.txt", "Book conversion sample text"); var pdf = Success(await Convert([input], "pdf"));
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.IsTrue(doc.NumberOfPages >= 1); StringAssert.Contains(doc.Information.Subject, "LOPATA.Provenance=");
        StringAssert.Contains(string.Join(" ", doc.GetPages().Select(p => p.Text)), "Book conversion sample text");
    }
    [TestMethod]
    public void CapabilityMatrixDoesNotPromiseGraphicOutputForText()
    {
        Assert.IsFalse(BookConversionCapabilities.GetOutputFormats(["a.txt"]).Contains("cbz"));
        Assert.IsTrue(BookConversionCapabilities.GetOutputFormats(["a.png", "b.tiff"]).Contains("cbz"));
        Assert.IsFalse(BookConversionFormats.OutputExtensions.Contains("cbr"));
        Assert.IsFalse(BookConversionFormats.InputExtensions.Contains("fb3"));
    }
    [TestMethod]
    public async Task HtmlCannotEmbedDisguisedPrivateTextAsAnImage()
    {
        Source("private.png", "not an image; private text must never be embedded");
        var html = Source("disguised.html", "<p>Allowed text</p><img src='private.png'>");
        var result = await Convert([html], "epub"); Assert.AreEqual(0, result.Outputs.Length); Assert.AreEqual(1, result.Failures.Length);
    }
    [TestMethod]
    public async Task HtmlCssDoesNotExportExternalOrLocalFileUrls()
    {
        var html = Source("css.html", "<html><head><style>p{background-image:url('file:///C:/private.png')}</style></head><body><p>Visible text</p></body></html>");
        var result = await Convert([html], "html"); var output = Success(result);
        Assert.IsFalse(File.ReadAllText(output).Contains("file:///", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(File.ReadAllText(output), "Visible text"); Assert.IsTrue(result.Warnings.Length > 0);
    }
    [TestMethod]
    public async Task DocxTablePackagePassesIndependentSchemaValidation()
    {
        var html = Source("table.html", "<h1>Таблица</h1><table><tr><th>Имя</th><th>Число</th></tr><tr><td>Ёжик</td><td>12</td></tr></table><p>Конец.</p>");
        var path = Success(await Convert([html], "docx"));
        using var document = WordprocessingDocument.Open(path, false);
        var problems = new OpenXmlValidator().Validate(document).Take(10).ToArray();
        Assert.AreEqual(0, problems.Length, string.Join("; ", problems.Select(p => p.Description)));
        StringAssert.Contains(document.MainDocumentPart?.Document?.InnerText ?? "", "Ёжик");
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SvgAttachmentsPermitLocalShapesButRejectExternalFileReferences(bool external)
    {
        var text = Source("svg-source.txt", "Safe book content");
        var epub = Success(await Convert([text], "epub"));
        using (var zip = ZipFile.Open(epub, ZipArchiveMode.Update))
        {
            var opfEntry = zip.GetEntry("OEBPS/content.opf")!; XDocument opf;
            using (var reader = opfEntry.Open()) opf = XDocument.Load(reader);
            XNamespace ns = "http://www.idpf.org/2007/opf";
            opf.Root!.Element(ns + "manifest")!.Add(new XElement(ns + "item", new XAttribute("id", "svg"), new XAttribute("href", "drawing.svg"), new XAttribute("media-type", "image/svg+xml")));
            opfEntry.Delete(); using (var writer = zip.CreateEntry("OEBPS/content.opf").Open()) opf.Save(writer);
            using var svg = new StreamWriter(zip.CreateEntry("OEBPS/drawing.svg").Open());
            svg.Write(external ? "<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///C:/private.png'/></svg>" : "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><rect width='20' height='20' fill='green'/></svg>");
        }
        var result = await Convert([epub], "html");
        Assert.AreEqual(external ? 0 : 1, result.Outputs.Length); Assert.AreEqual(external ? 1 : 0, result.Failures.Length);
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task EpubEncryptionGuardRejectsActualEncryptedDataButAllowsEmptyDeclaration(bool encrypted)
    {
        var source = Source("encryption-source.txt", "A safe source book");
        var epub = Success(await Convert([source], "epub"));
        using (var zip = ZipFile.Open(epub, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write(encrypted ? "<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><EncryptedData xmlns='http://www.w3.org/2001/04/xmlenc#'/></encryption>" : "<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'/>");
        }
        var before = SHA256.HashData(File.ReadAllBytes(epub));
        var result = await Convert([epub], "fb2");
        Assert.AreEqual(encrypted ? 0 : 1, result.Outputs.Length); Assert.AreEqual(encrypted ? 1 : 0, result.Failures.Length);
        CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(epub)));
    }
    [TestMethod]
    [DataRow("html")]
    [DataRow("md")]
    public async Task EpubCssImagesRemainEmbeddedInStandaloneHtmlAndMarkdown(string format)
    {
        byte[] image;
        using (var bitmap = new SKBitmap(12, 10)) { bitmap.Erase(SKColors.Blue); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); image = data.ToArray(); }
        var source = Source("css-source.txt", "Text with a CSS background.");
        var epub = Success(await Convert([source], "epub"));
        using (var zip = ZipFile.Open(epub, ZipArchiveMode.Update))
        {
            var opfEntry = zip.GetEntry("OEBPS/content.opf")!; XDocument opf;
            using (var stream = opfEntry.Open()) opf = XDocument.Load(stream);
            XNamespace ns = "http://www.idpf.org/2007/opf";
            opf.Root!.Element(ns + "manifest")!.Add(new XElement(ns + "item", new XAttribute("id", "css"), new XAttribute("href", "styles/page.css"), new XAttribute("media-type", "text/css")), new XElement(ns + "item", new XAttribute("id", "bg"), new XAttribute("href", "images/bg.png"), new XAttribute("media-type", "image/png")));
            opfEntry.Delete(); using (var output = zip.CreateEntry("OEBPS/content.opf").Open()) opf.Save(output);
            var textEntry = zip.GetEntry("OEBPS/book.xhtml")!; XDocument text;
            using (var stream = textEntry.Open()) text = XDocument.Load(stream);
            XNamespace xhtml = "http://www.w3.org/1999/xhtml";
            text.Root!.Element(xhtml + "head")!.Add(new XElement(xhtml + "link", new XAttribute("rel", "stylesheet"), new XAttribute("href", "styles/page.css")));
            textEntry.Delete(); using (var output = zip.CreateEntry("OEBPS/book.xhtml").Open()) text.Save(output);
            using (var css = new StreamWriter(zip.CreateEntry("OEBPS/styles/page.css").Open())) css.Write("p{background-image:url('../images/bg.png')}");
            using (var output = zip.CreateEntry("OEBPS/images/bg.png").Open()) output.Write(image);
        }
        var result = Success(await Convert([epub], format)); var outputText = File.ReadAllText(result);
        StringAssert.Contains(outputText, "background-image:url('data:image/png;base64," + System.Convert.ToBase64String(image));
        StringAssert.Contains(outputText, "Text with a CSS background.");
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(outputText, @"url\(['"" ]?asset\d+\.png", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Fb2SvgAttachmentsUseTheSameExternalResourceGuard(bool external)
    {
        var svg = external ? "<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///C:/private.png'/></svg>" : "<svg xmlns='http://www.w3.org/2000/svg'><rect width='20' height='20' fill='green'/></svg>";
        var source = Source("svg.fb2", "<FictionBook xmlns='http://www.gribuser.ru/xml/fictionbook/2.0' xmlns:l='http://www.w3.org/1999/xlink'><description><title-info><genre>unrecognised</genre><author><nickname>Author</nickname></author><book-title>SVG test</book-title><lang>en</lang></title-info><document-info><author><nickname>Author</nickname></author><date>2026-10-19</date><id>test</id><version>1.0</version></document-info></description><body><section><p>Visible text.</p><image l:href='#svg'/></section></body><binary id='svg' content-type='image/svg+xml'>" + System.Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) + "</binary></FictionBook>");
        var result = await Convert([source], "epub");
        Assert.AreEqual(external ? 0 : 1, result.Outputs.Length); Assert.AreEqual(external ? 1 : 0, result.Failures.Length);
    }
    [TestMethod]
    [DataRow("Hello world", "Hello world")]
    [DataRow("Hello <b>world</b>!", "Hello world!")]
    [DataRow("<div><p>red</p><p>blue</p></div><table><tr><td>alpha</td><td>beta</td></tr></table>", "red blue alpha beta")]
    public async Task PdfRetainsDirectBodyTextAndNestedBlockBoundaries(string body, string expected)
    {
        var source = Source("pdf-body.html", "<html><body>" + body + "</body></html>");
        var path = Success(await Convert([source], "pdf"));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var words = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        Assert.AreEqual(expected, words);
    }
    [TestMethod]
    public async Task DocxNestedTableIsNotDuplicatedAndPassesSchemaValidation()
    {
        var source = Source("nested-table.html", "<table><tr><td>A<table><tr><td>B</td></tr></table></td></tr></table>");
        var path = Success(await Convert([source], "docx"));
        using var document = WordprocessingDocument.Open(path, false);
        var text = document.MainDocumentPart?.Document?.InnerText ?? "";
        Assert.AreEqual(1, text.Count(c => c == 'A')); Assert.AreEqual(1, text.Count(c => c == 'B'));
        var errors = new OpenXmlValidator().Validate(document).Take(10).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("; ", errors.Select(e => e.Description)));
    }
    private sealed class ImmediateProgress(Action<BookConversionProgress> action) : IProgress<BookConversionProgress> { public void Report(BookConversionProgress value) => action(value); }
}
