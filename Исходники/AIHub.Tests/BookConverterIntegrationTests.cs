using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using AIHub.Services;
using AIHub.Services.Books;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookConverterIntegrationTests
{
    [TestMethod]
    public async Task RealServiceNativeAndExternalFormatsPassIndependentReaders()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_CONVERTER_INTEGRATION") != "1") Assert.Inconclusive("Opt-in real converters test.");
        Assert.IsNotNull(BookConverterRuntime.FindCalibre(), "The actual pinned calibre bundle is required.");
        var root = Environment.GetEnvironmentVariable("LOPATA_BOOK_INTEGRATION_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "LOPATA-BookIntegration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Источник.html"); var picture = Path.Combine(root, "page.png");
        using (var bitmap = new SKBitmap(32, 24))
        {
            using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.ForestGreen);
            using var image = SKImage.FromBitmap(bitmap); using var bytes = image.Encode(SKEncodedImageFormat.Png, 100); await File.WriteAllBytesAsync(picture, bytes.ToArray());
        }
        const string start = "Редкий ёж бережёт звёзды.";
        const string end = "Контрольный конец произведения.";
        await File.WriteAllTextAsync(source, "<!DOCTYPE html><html lang='ru'><head><meta charset='utf-8'><meta name='author' content='Исходный автор'><title>Проверочная книга</title></head><body><h1 id='chapter'>Глава первая</h1><p>" + start + "</p><p><a href='#note'>Примечание</a></p><img src='page.png' alt='Иллюстрация'><table><tr><td>Ячейка один</td><td>Ячейка два</td></tr></table><aside id='note'><p>Точный текст примечания.</p></aside><h2>Глава вторая</h2><p>" + end + "</p></body></html>", new UTF8Encoding(false));
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var beforePolicy = ComponentLicenseGate.ConfirmAsync;
        var results = new List<object>(); var errors = new List<string>();
        var reviewEnabled = Environment.GetEnvironmentVariable("LOPATA_BOOK_INTEGRATION_REVIEW") == "1";
        var editReview = reviewEnabled && Environment.GetEnvironmentVariable("LOPATA_BOOK_REVIEW_EDIT") == "1";
        const string editedStart = "Редкий ёж проверил сохранённые правки.";
        var expectedStart = editReview ? editedStart : start;
        var reviewed = new List<string>();
        BookConversionService Service(string format) => new(reviewEnabled ? async (session, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.IsTrue(File.Exists(session.PreviewPath), "Review must receive the actual prepared target.");
            Assert.AreEqual(format, session.OutputFormat);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, format)), "No destination should be created before review approval.");
            if (format != "cbz")
            {
                var html = session.Document.GetHtml();
                Assert.IsTrue(html.Contains(start) && html.Contains(end), "Review must contain text read from the prepared result: " + format);
                if (format == "odt") Assert.IsTrue(html.Contains("data:image/", StringComparison.Ordinal), "ODT review must retain the actual embedded illustration.");
                if (editReview)
                {
                    var document = session.Document.Clone();
                    var block = document.Blocks.First(value => value.Text.Contains(start));
                    document.UpdateBlockText(block.Id, block.Text.Replace(start, editedStart, StringComparison.Ordinal));
                    session.SetDocument(document);
                    Assert.IsTrue(session.IsDirty);
                    await session.RefreshPreviewAsync(token);
                    Assert.IsFalse(session.IsDirty);
                    Assert.IsTrue(session.Document.GetHtml().Contains(editedStart), "The refreshed preview must read back the edited target: " + format);
                    if (format == "odt") Assert.IsTrue(session.Document.GetHtml().Contains("data:image/", StringComparison.Ordinal), "ODT editing must keep the illustration after re-export.");
                }
            }
            reviewed.Add(format);
            return BookReviewDecision.Save;
        } : null);
        ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask; // Explicit stand policy; no receipt/user preference is changed.
        try
        {
            var formats = new[] { "epub", "fb2", "docx", "html", "md", "txt", "pdf", "rtf", "azw3", "mobi", "kepub", "odt" };
            foreach (var format in formats)
            {
                if (format == "odt" && BookConverterRuntime.FindLibreOffice() is null) { errors.Add("LibreOffice was not prepared for ODT."); continue; }
                var output = await Service(format).ConvertAsync(new() { Sources = [source], OutputFormat = format, OutputDirectory = Path.Combine(root, format) });
                results.Add(new { format, output });
                if (output.Failures.Length > 0 || output.Outputs.Length != 1) { errors.Add(format + ": " + string.Join("; ", output.Failures.Select(f => f.Message))); continue; }
                try
                {
                    var path = output.Outputs[0];
                    if (format == "fb2")
                    {
                        // Full official XSD validation is performed separately with lxml in
                        // BookConverterStand: System.Xml cannot compile that legacy schema
                        // (element type consistency), even with UPA checking disabled.
                        var document = XDocument.Load(path);
                        var text = string.Join(" ", document.Descendants().Where(e => e.Name.LocalName is "p" or "td").Select(e => e.Value));
                        Assert.IsTrue(text.Contains(expectedStart) && text.Contains(end));
                    }
                    else if (format == "docx")
                    {
                        using var document = WordprocessingDocument.Open(path, false);
                        var validation = new OpenXmlValidator().Validate(document).Take(6).Select(e => e.Description).ToArray();
                        if (validation.Length > 0) errors.Add("DOCX schema: " + string.Join("; ", validation));
                        var main = document.MainDocumentPart!;
                        var text = main.Document!.InnerText;
                        Assert.IsTrue(text.Contains(expectedStart) && text.Contains(end));
                        Assert.IsTrue(main.ImageParts.Any());
                    }
                    else if (format == "pdf")
                    {
                        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
                        Assert.IsTrue(document.NumberOfPages > 0);
                        var text = string.Join(" ", document.GetPages().Select(p => UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p)));
                        Assert.IsTrue(text.Contains(expectedStart) && text.Contains(end), text);
                        Assert.IsTrue(document.Information.Subject?.Contains("LOPATA.Provenance=") == true);
                    }
                    else if (format == "txt") { var text = await File.ReadAllTextAsync(path); Assert.IsTrue(text.Contains(expectedStart) && text.Contains(end)); }
                    else if (format is "epub" or "kepub")
                    {
                        using var zip = ZipFile.OpenRead(path); Assert.AreEqual("mimetype", zip.Entries[0].FullName);
                        Assert.IsTrue(zip.Entries.Any(e => e.Name.EndsWith(".png")));
                    }
                }
                catch (Exception ex) { errors.Add(format + " independent read: " + ex.Message); }
            }
            var graphic = await Service("cbz").ConvertAsync(new() { Sources = [picture], OutputFormat = "cbz", OutputDirectory = Path.Combine(root, "cbz") });
            results.Add(new { format = "cbz", output = graphic }); if (graphic.Failures.Length > 0) errors.Add("CBZ: " + string.Join("; ", graphic.Failures.Select(f => f.Message)));
            var legacy = Path.Combine(AppDataPaths.ProjectRoot!, "Тесты", "BookConverterStand", "office-output", "rich.doc");
            Assert.IsTrue(File.Exists(legacy), "The official-office legacy DOC stand fixture is required.");
            var legacyHash = SHA256.HashData(await File.ReadAllBytesAsync(legacy));
            var legacyResult = await new BookConversionService().ConvertAsync(new() { Sources = [legacy], OutputFormat = "fb2", OutputDirectory = Path.Combine(root, "legacy-doc") });
            results.Add(new { format = "DOC-to-FB2", output = legacyResult });
            if (legacyResult.Failures.Length > 0 || legacyResult.Outputs.Length != 1) errors.Add("DOC-to-FB2: " + string.Join("; ", legacyResult.Failures.Select(f => f.Message)));
            else
            {
                var text = XDocument.Load(legacyResult.Outputs[0]).Root!.Value;
                Assert.IsTrue(text.Contains(start) && text.Contains(end));
                Assert.IsTrue(text.Contains("Важное примечание о сохранности."));
                var reverse = await new BookConversionService().ConvertAsync(new() { Sources = legacyResult.Outputs, OutputFormat = "odt", OutputDirectory = Path.Combine(root, "legacy-reverse") });
                results.Add(new { format = "FB2-to-ODT", output = reverse });
                if (reverse.Failures.Length > 0 || reverse.Outputs.Length != 1) errors.Add("FB2-to-ODT: " + string.Join("; ", reverse.Failures.Select(f => f.Message)));
                else
                {
                    using var package = ZipFile.OpenRead(reverse.Outputs[0]);
                    using var xml = package.GetEntry("content.xml")!.Open();
                    var resultText = XDocument.Load(xml).Root!.Value;
                    Assert.IsTrue(resultText.Contains(start) && resultText.Contains(end));
                }
            }
            CollectionAssert.AreEqual(legacyHash, SHA256.HashData(await File.ReadAllBytesAsync(legacy)));
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = beforePolicy;
            await File.WriteAllTextAsync(Path.Combine(root, "integration-result.json"), JsonSerializer.Serialize(new { results, errors, reviewEnabled, editReview, reviewed }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        if (reviewEnabled) Assert.HasCount(13, reviewed, "All advertised output formats must support pre-save review.");
        Assert.HasCount(0, errors, string.Join(Environment.NewLine, errors));
    }
}
