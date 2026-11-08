using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Services;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderTests
{
    [TestMethod]
    public void SpreadNavigationAndZoomStayWithinTheDocument()
    {
        Assert.AreEqual(2, ReaderNavigation.SpreadStart(3));
        Assert.AreEqual(4, ReaderNavigation.Next(2, 5, true, 1));
        Assert.AreEqual(0, ReaderNavigation.Next(1, 5, true, -1));
        Assert.AreEqual(4, ReaderNavigation.Next(4, 5, false, 1));
        Assert.AreEqual(25, ReaderNavigation.ClampZoom(-100));
        Assert.AreEqual(400, ReaderNavigation.ClampZoom(1000));
        Assert.AreEqual(100, ReaderNavigation.ClampZoom(double.NaN));
    }

    [TestMethod]
    public void ComicPagesSortNaturallyRatherThanLexically()
    {
        var names = new[] { "page10.png", "page2.png", "page1.png", "page20.png" };
        Array.Sort(names, NaturalNameComparer.Instance);
        CollectionAssert.AreEqual(new[] { "page1.png", "page2.png", "page10.png", "page20.png" }, names);
        Assert.IsTrue(FileViewerService.UsesReader("book.fb2.zip"));
        Assert.IsFalse(FileViewerService.UsesReader("source.cs"));
        Assert.IsFalse(FileViewerService.UsesReader("archive.zip"));
    }

    [TestMethod]
    public void MarkupResolvesExplicitParentImagesAndRemovesActiveAndRemoteContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "reader-markup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "text"));
        try
        {
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(root, "figure.png"), encoded.ToArray());
            var source = Path.Combine(root, "text", "example.html");
            File.WriteAllText(source, "<h1>Пример</h1><img src='../figure.png'><img src='https://invalid.test/a.png'><script>alert(1)</script>");
            var html = ReaderMarkup.Prepare(source, CancellationToken.None);
            StringAssert.Contains(html, "data:image/png;base64,");
            StringAssert.Contains(html, "Пример");
            Assert.IsFalse(html.Contains("https://invalid.test", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(File.ReadAllText(source), "../figure.png");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OrdinaryZipIsRejectedAndOriginalRemainsUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "office-reader-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "not-a-book.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../../note.txt").Open())) writer.Write("This must not be extracted.");
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        try
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReaderPreparation.OpenAsync(path, null, CancellationToken.None));
            Assert.AreEqual("Reader.Fb2ZipRequired", error.Message);
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(path)));
            Assert.IsFalse(File.Exists(Path.Combine(root, "note.txt")));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; Directory.Delete(root, true); }
    }

    [TestMethod, TestCategory("Native")]
    public async Task WarmOfficeSessionReusesEngineAndCachedPreviewRemainsCorrect()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_NATIVE") != "1") Assert.Inconclusive("Explicit native reader run required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107");
        var source = Path.Combine(root, "user-suite", "AI_Office_TestSuite_RU_EN", "02_documents", "RU", "complex_review.docx");
        var other = Path.Combine(root, "user-suite", "AI_Office_TestSuite_RU_EN", "02_documents", "EN", "complex_review.docx");
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var timings = new List<object>();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var audit = ComponentAudit.CaptureOperation();
        var lease = ReaderOfficeSession.Retain();
        try
        {
            int? engine = null;
            foreach (var file in new[] { source, other })
            {
                var workspace = Path.Combine(ReaderPreparation.CacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
                try
                {
                    var copy = Path.Combine(workspace, "source.docx"); File.Copy(file, copy);
                    var start = DateTime.UtcNow;
                    var pdf = await ReaderOfficeExport.ExportAsync(file, copy, workspace, ".docx", limit.Token);
                    Assert.IsGreaterThan(100, new FileInfo(pdf).Length);
                    var worker = OwnedProcessRegistry.Shared.GetSnapshot().Single(row => row.Component == "Office.Reader");
                    if (engine is not null) Assert.AreEqual(engine.Value, worker.Pid, "Different documents must share the same engine.");
                    engine = worker.Pid;
                    timings.Add(new { step = file == source ? "first_engine_document" : "second_engine_document", ms = (DateTime.UtcNow - start).TotalMilliseconds });
                }
                finally { ReaderPreparation.DeleteWorkspace(workspace); }
            }
            byte[]? previous = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var start = DateTime.UtcNow;
                await using var document = await ReaderPreparation.OpenAsync(source, null, limit.Token);
                var hash = SHA256.HashData(await File.ReadAllBytesAsync(document.PdfPath!, limit.Token));
                if (previous is not null) CollectionAssert.AreEqual(previous, hash);
                previous = hash;
                Assert.IsTrue(File.Exists(await document.Backend.RenderAsync(0, 850, limit.Token)));
                timings.Add(new { step = attempt == 0 ? "prepare_and_display" : "cached_prepare_and_display", ms = (DateTime.UtcNow - start).TotalMilliseconds });
            }
            File.WriteAllText(Path.Combine(root, "speed-result.json"), System.Text.Json.JsonSerializer.Serialize(timings));
        }
        finally { await lease.DisposeAsync(); ComponentLicenseGate.ConfirmAsync = gate; }
        Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(row => row.Component == "Office.Reader"), "Closing the final reader must release the engine.");
    }

    [TestMethod, TestCategory("Native")]
    public async Task ExtraBookContainersInternalLinksAndScanSearchAreHandled()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_NATIVE") != "1") Assert.Inconclusive("Explicit native reader run required.");
        var root = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "OfficeReader_20261107");
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        using var audit = ComponentAudit.CaptureOperation();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            foreach (var name in new[] { "reader.mobi", "reader.azw3", "reader-store.cbr" })
            {
                await using var document = await ReaderPreparation.OpenAsync(Path.Combine(root, name), null, limit.Token);
                Assert.IsGreaterThan(0, document.Pages.Count);
                Assert.IsTrue(File.Exists(await document.Backend.RenderAsync(0, 850, limit.Token)));
                if (name.EndsWith(".cbr")) Assert.HasCount(4, document.Pages);
                else Assert.IsTrue((await document.FindAsync("Сноски", 0, limit.Token)).HasText);
            }
            var corpus = Path.Combine(root, "user-suite", "AI_Office_TestSuite_RU_EN");
            await using var book = await ReaderPreparation.OpenAsync(Path.Combine(corpus, "06_ebooks", "RU", "epub2_chapters_footnotes.epub"), null, limit.Token);
            Assert.IsNotNull((await book.FindAsync("внутренние ссылки", 0, limit.Token)).Page);
            Assert.IsTrue(Enumerable.Range(0, book.Pages.Count).Any(page => ReaderLinks.Read(book.PdfPath, page).Any(link => link.Page is not null)));
            await using var comments = await ReaderPreparation.OpenAsync(Path.Combine(corpus, "02_documents", "RU", "complex_review.docx"), null, limit.Token);
            Assert.IsTrue(Enumerable.Range(0, comments.Pages.Count).Any(page => ReaderLinks.Read(comments.PdfPath, page).Any(link => !string.IsNullOrWhiteSpace(link.Note))));
            await using var scan = await ReaderPreparation.OpenAsync(Path.Combine(corpus, "05_pdf", "RU", "image_only_no_OCR.pdf"), null, limit.Token);
            Assert.IsFalse((await scan.FindAsync("anything", 0, limit.Token)).HasText);
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }

    [TestMethod, TestCategory("Native")]
    public async Task RealOfficeBooksAndScansRenderWithoutChangingSources()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_READER_NATIVE") != "1") Assert.Inconclusive("Explicit native reader run required.");
        var root = AppDataPaths.ProjectRoot!;
        var sources = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LOPATA_YouTube_Publication_Template_Updated_0.7.1.docx"),
            @"D:\Yandex.Disk.Files\Yandex.Disk\Архив\Расчет дерева 2.xlsx",
            @"D:\Yandex.Disk.Files\Yandex.Disk\Архив\Русский лес прайс 01.02.21.xlsx",
            Path.Combine(root, "Тесты", "BookConverterStand", "corpus", "rich.fb2"),
            Path.Combine(root, "Тесты", "BookConverterStand", "calibre-output", "rich_fb2.epub"),
            Path.Combine(root, "Тесты", "BookConverterStand", "review-output-01", "cbz", "page.cbz"),
            Path.Combine(root, "Тесты", "OfficeReader_20261107", "production.pdf"),
            Path.Combine(root, "Тесты", "OfficeReader_20261107", "DjVuLibre", "extracted", "doc", "lizard2002.djvu")
        };
        var gate = ComponentLicenseGate.ConfirmAsync; ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
        var results = new List<object>();
        using var audit = ComponentAudit.CaptureOperation();
        try
        {
            foreach (var source in sources)
            {
                Assert.IsTrue(File.Exists(source), source);
                var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var start = DateTime.UtcNow;
                var document = await ReaderPreparation.OpenAsync(source, null, limit.Token);
                var workspace = document.Workspace;
                try
                {
                    Assert.IsGreaterThan(0, document.Pages.Count);
                    foreach (var page in new[] { 0, document.Pages.Count - 1 }.Distinct())
                    {
                        var image = await document.Backend.RenderAsync(page, 1000, limit.Token);
                        Assert.IsGreaterThan(100, new FileInfo(image).Length);
                        using (var codec = SkiaSharp.SKCodec.Create(image))
                        { Assert.IsLessThanOrEqualTo(1002, codec.Info.Width); Assert.IsLessThanOrEqualTo(4098, codec.Info.Height); }
                        var copy = Path.Combine(root, "Тесты", "OfficeReader_20261107", Path.GetFileName(source) + ".page" + (page + 1) + ".png");
                        File.Copy(image, copy, true);
                    }
                    results.Add(new { file = Path.GetFileName(source), pages = document.Pages.Count, seconds = (DateTime.UtcNow - start).TotalSeconds });
                    CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
                }
                finally { await document.DisposeAsync(); }
                Assert.IsFalse(Directory.Exists(workspace), "Private pages should be removed when the document closes.");
            }
            File.WriteAllText(Path.Combine(root, "Тесты", "OfficeReader_20261107", "native-result.json"), System.Text.Json.JsonSerializer.Serialize(results));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }
}
