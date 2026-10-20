using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AIHub.Services.Books;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass]
public sealed class BookConversionReviewTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "LOPATA-BookReviewTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Source(string name, string text) { var path = Path.Combine(_root, name); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    private BookConversionRequest Request(params string[] paths) => new() { Sources = paths, OutputFormat = "epub", OutputDirectory = Path.Combine(_root, "out") };
    private static string Success(BookConversionResult result)
    {
        Assert.AreEqual(0, result.Failures.Length, string.Join("; ", result.Failures.Select(f => f.Message))); Assert.AreEqual(1, result.Outputs.Length); return result.Outputs[0];
    }
    [TestMethod]
    public async Task ExplicitSavePublishesExactReviewedBytesAndNothingExistsBeforeApproval()
    {
        var source = Source("review.txt", "A book awaiting explicit approval."); var request = Request(source); byte[]? reviewed = null; string? temporary = null; var calls = 0;
        var service = new BookConversionService((session, token) =>
        {
            calls++; temporary = session.PreviewPath; Assert.IsTrue(File.Exists(temporary)); Assert.IsFalse(Directory.Exists(request.OutputDirectory));
            Assert.AreEqual("epub", session.OutputFormat); Assert.AreEqual("review.txt", session.SourceName); Assert.IsTrue(session.CanSave);
            reviewed = File.ReadAllBytes(temporary); return Task.FromResult(BookReviewDecision.Save);
        });
        var output = Success(await service.ConvertAsync(request));
        Assert.AreEqual(1, calls); Assert.IsNotNull(reviewed); CollectionAssert.AreEqual(reviewed, File.ReadAllBytes(output));
        Assert.IsNotNull(temporary); Assert.IsFalse(File.Exists(temporary));
    }
    [TestMethod]
    public async Task SkipAndCancelDoNotPublishAndKeepAnEarlierApprovedBook()
    {
        var first = Source("1.txt", "First approved book."); var second = Source("2.txt", "Skipped book."); var third = Source("3.txt", "Cancelled book.");
        var calls = 0; var phases = new List<string>();
        var service = new BookConversionService((_, _) => Task.FromResult(++calls switch { 1 => BookReviewDecision.Save, 2 => BookReviewDecision.Skip, _ => BookReviewDecision.Cancel }));
        var result = await service.ConvertAsync(Request(first, second, third), new ProgressCapture(p => phases.Add(p.Phase)));
        Assert.AreEqual(1, result.Outputs.Length); Assert.AreEqual(0, result.Failures.Length); Assert.IsTrue(result.Cancelled);
        CollectionAssert.AreEqual(new[] { second }, result.Skipped); Assert.IsTrue(phases.Contains("skipped"));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_root, "out")).Length);
    }
    [TestMethod, DataRow("html"), DataRow("txt")]
    public async Task MarkdownEditsUseTheActualNewFormatAndNeverRewriteTheSource(string format)
    {
        var source = Source("source.md", "# Original title\n\nA **formatted** paragraph.");
        var original = File.ReadAllBytes(source);
        var service = new BookConversionService(async (session, token) =>
        {
            Assert.AreEqual("." + format, Path.GetExtension(session.PreviewPath));
            Assert.AreNotEqual(source, session.PreviewPath);
            var actualHtml = session.Document.GetHtml();
            if (format == "txt") Assert.IsFalse(actualHtml.Contains("<strong", StringComparison.OrdinalIgnoreCase), "TXT preview must reflect formatting already lost in conversion, not reuse the source Markdown DOM.");
            var document = session.Document;
            var block = document.Blocks.First(value => value.Text.Contains("Original title", StringComparison.Ordinal));
            document.UpdateBlockText(block.Id, block.Text.Replace("Original title", "Edited converted title", StringComparison.Ordinal));
            session.SetDocument(document); await session.RefreshPreviewAsync(token);
            StringAssert.Contains(File.ReadAllText(session.PreviewPath), "Edited converted title");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            return BookReviewDecision.Save;
        });
        var output = Success(await service.ConvertAsync(Request(source) with { OutputFormat = format }));
        StringAssert.Contains(File.ReadAllText(output), "Edited converted title");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
    }
    [TestMethod]
    public async Task DirtySaveWithoutRenderingIsRejectedInsteadOfSavingOldOrUnseenBytes()
    {
        var source = Source("dirty.txt", "Original paragraph.");
        var service = new BookConversionService((session, _) =>
        {
            var document = session.Document.Clone(); document.UpdateBlockText(document.Blocks.First().Id, "Unrendered changed paragraph."); session.SetDocument(document);
            Assert.IsTrue(session.IsDirty); Assert.IsFalse(session.CanSave); return Task.FromResult(BookReviewDecision.Save);
        });
        var result = await service.ConvertAsync(Request(source)); Assert.AreEqual(0, result.Outputs.Length); Assert.AreEqual(1, result.Failures.Length);
        StringAssert.Contains(result.Failures[0].Message, "BookConverter.Review.Unrefreshed"); Assert.IsFalse(Directory.Exists(Path.Combine(_root, "out")));
        Assert.AreEqual("Original paragraph.", File.ReadAllText(source));
    }
    [TestMethod]
    public async Task EditedRenderedStageRetainsImagesAnchorsInlineFormattingMetadataAndOriginalSource()
    {
        var png = Path.Combine(_root, "picture.png"); using (var bitmap = new SKBitmap(20, 16)) { bitmap.Erase(SKColors.Magenta); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var imageOutput = File.Create(png); data.SaveTo(imageOutput); }
        var source = Source("rich.html", "<html><head><title>Review source</title></head><body><p id='main'>Hello <strong>world</strong>! <a href='#note'>note</a><img src='picture.png'></p><p id='note'>A retained note.</p></body></html>");
        var originalHash = SHA256.HashData(File.ReadAllBytes(source)); byte[]? approved = null;
        var service = new BookConversionService(async (session, token) =>
        {
            var before = File.ReadAllBytes(session.PreviewPath); var detached = session.Document;
            var assetSnapshot = detached.Assets.First(); var originalAssetByte = assetSnapshot.Value[0];
            assetSnapshot.Value[0] ^= 0xff;
            Assert.AreEqual(originalAssetByte, detached.Assets[assetSnapshot.Key][0]);
            Assert.AreEqual(originalAssetByte, session.Document.Assets[assetSnapshot.Key][0]);
            var block = detached.Blocks.First(b => b.Text.Contains("Hello world!", StringComparison.Ordinal));
            detached.UpdateBlockText(block.Id, "Hello 🌟 bright world! note");
            Assert.IsFalse(session.IsDirty); // Editing a detached snapshot cannot bypass SetDocument/preview tracking.
            session.SetDocument(detached); Assert.IsTrue(session.IsDirty);
            await session.RefreshPreviewAsync(token);
            Assert.IsFalse(session.IsDirty); Assert.IsTrue(session.CanSave); Assert.AreEqual(session.Revision, session.PreviewRevision);
            StringAssert.Contains(session.Document.GetHtml(), "bright world");
            using (var zip = ZipFile.OpenRead(session.PreviewPath))
            {
                using var reader = new StreamReader(zip.GetEntry("OEBPS/book.xhtml")!.Open()); var body = XDocument.Parse(await reader.ReadToEndAsync(token));
                Assert.IsTrue(body.Descendants().Any(e => e.Name.LocalName == "strong" && e.Value.Contains("bright world", StringComparison.Ordinal)));
                var ids = body.Descendants().Attributes("id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
                Assert.IsTrue(body.Descendants().Where(e => e.Name.LocalName == "a").Attributes("href").Any(a => a.Value.StartsWith('#') && ids.Contains(a.Value[1..])));
                Assert.IsTrue(body.Descendants().Any(e => e.Name.LocalName == "img")); Assert.IsTrue(zip.Entries.Any(e => e.FullName.EndsWith(".png")));
                using var metadata = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open()); StringAssert.Contains(await metadata.ReadToEndAsync(token), "lopata:provenance");
            }
            approved = File.ReadAllBytes(session.PreviewPath); Assert.IsFalse(before.SequenceEqual(approved)); return BookReviewDecision.Save;
        });
        var output = Success(await service.ConvertAsync(Request(source))); Assert.IsNotNull(approved); CollectionAssert.AreEqual(approved, File.ReadAllBytes(output));
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
    }
    [TestMethod]
    public async Task CancelledRefreshLeavesPreviousPrivatePreviewIntactAndDirty()
    {
        var source = Source("cancel-refresh.txt", "Original book text.");
        var service = new BookConversionService(async (session, _) =>
        {
            var before = File.ReadAllBytes(session.PreviewPath); var document = session.Document; document.UpdateBlockText(document.Blocks.First().Id, "Changed book text."); session.SetDocument(document);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => session.RefreshPreviewAsync(cancel.Token));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(session.PreviewPath)); Assert.IsTrue(session.IsDirty); Assert.IsFalse(session.IsRefreshing);
            return BookReviewDecision.Skip;
        });
        var result = await service.ConvertAsync(Request(source)); Assert.AreEqual(0, result.Outputs.Length); Assert.AreEqual(0, result.Failures.Length); Assert.AreEqual(1, result.Skipped.Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "out")));
    }
    [TestMethod]
    public async Task SelfContainedSvgAndCssImageSurviveReadingAndEditingActualHtmlTarget()
    {
        byte[] png; using (var bitmap = new SKBitmap(12, 8)) { bitmap.Erase(SKColors.Blue); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); png = data.ToArray(); }
        var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10' fill='green'/></svg>";
        var imageUri = "data:image/png;base64," + System.Convert.ToBase64String(png); var svgUri = "data:image/svg+xml;base64," + System.Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        var source = Source("self-contained.html", "<html><head><style>p{background-image:url('" + imageUri + "')}</style></head><body><p>Visible original.</p><img src='" + svgUri + "'></body></html>");
        var service = new BookConversionService(async (session, token) =>
        {
            StringAssert.Contains(session.Document.GetHtml(), svgUri); StringAssert.Contains(session.Document.GetHtml(), imageUri);
            var document = session.Document; var block = document.Blocks.First(b => b.Text.Contains("Visible original.", StringComparison.Ordinal));
            document.UpdateBlockText(block.Id, "Visible reviewed."); session.SetDocument(document); await session.RefreshPreviewAsync(token);
            StringAssert.Contains(session.Document.GetHtml(), svgUri); StringAssert.Contains(session.Document.GetHtml(), imageUri); return BookReviewDecision.Save;
        });
        var output = Success(await service.ConvertAsync(Request(source) with { OutputFormat = "html" }));
        var text = File.ReadAllText(output); StringAssert.Contains(text, "Visible reviewed."); StringAssert.Contains(text, svgUri); StringAssert.Contains(text, imageUri);
    }
    [TestMethod]
    public async Task ReviewedEpubRetainsInlineSvgNamespaceXlinkAndRebasedCssResources()
    {
        byte[] png; using (var bitmap = new SKBitmap(20, 10)) { bitmap.Erase(SKColors.Green); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); png = data.ToArray(); }
        var source = Path.Combine(_root, "inline-svg.epub");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            void Text(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            Text("mimetype", "application/epub+zip");
            Text("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='OEBPS/content.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Text("OEBPS/content.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:uuid:test</dc:identifier><dc:title>SVG resource book</dc:title><dc:language>en</dc:language></metadata><manifest><item id='text' href='chapters/book.xhtml' media-type='application/xhtml+xml' properties='svg'/><item id='css' href='styles/main.css' media-type='text/css'/><item id='image' href='images/cover.png' media-type='image/png'/></manifest><spine><itemref idref='text'/></spine></package>");
            Text("OEBPS/chapters/book.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>SVG resource book</title><link rel='stylesheet' href='../styles/main.css'/></head><body><p>Original text.</p><svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 20 10'><image width='20' height='10' xlink:href='../images/cover.png'/></svg></body></html>");
            Text("OEBPS/styles/main.css", "@page{margin:5pt 0;padding:0}p{background-image:url('../images/cover.png')}");
            using var output = zip.CreateEntry("OEBPS/images/cover.png").Open(); output.Write(png);
        }
        void Verify(string path)
        {
            using var zip = ZipFile.OpenRead(path); XDocument body; using (var stream = zip.GetEntry("OEBPS/book.xhtml")!.Open()) body = XDocument.Load(stream);
            XNamespace svg = "http://www.w3.org/2000/svg", xlink = "http://www.w3.org/1999/xlink";
            var image = body.Descendants(svg + "image").Single(); var imageHref = image.Attribute(xlink + "href")?.Value;
            Assert.IsNotNull(imageHref, body.ToString()); Assert.IsNotNull(zip.GetEntry("OEBPS/" + imageHref));
            Assert.IsFalse(body.Descendants((XNamespace)"http://www.w3.org/1999/xhtml" + "svg").Any());
            var css = string.Join("\n", body.Descendants().Where(e => e.Name.LocalName == "style").Select(e => e.Value));
            StringAssert.Contains(css, "background-image:url('");
            XDocument nav; using (var stream = zip.GetEntry("OEBPS/nav.xhtml")!.Open()) nav = XDocument.Load(stream);
            Assert.AreEqual(css, string.Join("\n", nav.Descendants().Where(e => e.Name.LocalName == "style").Select(e => e.Value)));
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(css, @"url\(['"" ]?(?<file>[^)'""]+)")) Assert.IsNotNull(zip.GetEntry("OEBPS/" + match.Groups["file"].Value));
            XDocument opf; using (var stream = zip.GetEntry("OEBPS/content.opf")!.Open()) opf = XDocument.Load(stream);
            Assert.IsTrue(opf.Descendants().Any(e => e.Name.LocalName == "item" && e.Attribute("id")?.Value == "text" && e.Attribute("properties")?.Value.Split(' ').Contains("svg") == true));
            foreach (var stylesheet in zip.Entries.Where(e => e.FullName.EndsWith(".css")))
            {
                using var reader = new StreamReader(stylesheet.Open());
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), @"url\(['"" ]?(?<file>[^)'""]+)"))
                    Assert.IsNotNull(zip.GetEntry("OEBPS/" + match.Groups["file"].Value));
            }
        }
        var service = new BookConversionService(async (session, token) =>
        {
            Verify(session.PreviewPath); var document = session.Document; var block = document.Blocks.First(b => b.Text.Contains("Original text.", StringComparison.Ordinal));
            document.UpdateBlockText(block.Id, "Reviewed text."); session.SetDocument(document); await session.RefreshPreviewAsync(token); Verify(session.PreviewPath); return BookReviewDecision.Save;
        });
        var result = await service.ConvertAsync(Request(source) with { Options = new BookConversionOptions { FontSize = 13 } }); // Force structural conversion rather than unchanged same-format copy.
        Verify(Success(result));
    }
    private sealed class ProgressCapture(Action<BookConversionProgress> callback) : IProgress<BookConversionProgress> { public void Report(BookConversionProgress value) => callback(value); }
}
