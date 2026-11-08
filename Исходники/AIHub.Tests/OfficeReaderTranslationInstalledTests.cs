using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using SkiaSharp;
using UglyToad.PdfPig;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderTranslationInstalledTests
{
    [TestMethod]
    public async Task InstalledCoreTranslatesBothDirectionsWithArtworkAndSeparateExport()
    {
        var folder = Environment.GetEnvironmentVariable("LOPATA_READER_TRANSLATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(folder)) { Assert.Inconclusive("Opt in to installed-model translation."); return; }
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.pdf"); MakePdf(source);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var library = new ManagedModelLibraryStore(); var card = library.Load(ManagedModelCatalog.CoreArtifactId); Assert.IsNotNull(card);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using (var prepare = new ReaderTextPreparation(library))
            Assert.IsTrue(await prepare.PrepareAsync(card, false, null, timeout.Token), "No downloads in this probe.");
        var workspace = Path.Combine(folder, "source-view"); Directory.CreateDirectory(workspace);
        var backend = await ReaderPdfPages.OpenAsync(source, workspace, timeout.Token);
        await using var document = new ReaderDocument(source, workspace, backend, source);
        var previous = ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current = new(new(Path.Combine(folder, "background", "operation.json")));
        using var session = new ReaderSpeechSession(Path.Combine(folder, "temporary"));
        try
        {
            foreach (var (index, from, to) in new[] { (0, "ru", "en"), (1, "en", "ru") })
            {
                var input = Path.Combine(session.DirectoryPath, from + "-" + to); Directory.CreateDirectory(input);
                var layout = await ReaderTranslationLayoutReader.ReadAsync(document, new(index, 1), input, timeout.Token);
                await File.WriteAllTextAsync(Path.Combine(folder, from + "-input.json"), ReaderTranslationProtocol.Input(from, to, layout.Pieces), timeout.Token);
                var timer = Stopwatch.StartNew();
                var request = new ReaderTranslationRequest(Guid.NewGuid().ToString("N"), layout, card, from, to, "Reader translation probe");
                var entries = new Dictionary<string, OfficeAiEntry>();
                var journal = new OfficeAiJournal("translation-source", "Translation", (entry, _) =>
                {
                    entries[entry.Id] = entry;
                    return Task.CompletedTask;
                });
                var result = await new ReaderTranslationWork(session, () => new ReaderTranslationRuntime(journal))
                    .GenerateAsync(request, null, timeout.Token);
                var expected = ReaderTranslationProtocol.Batches(layout.Pieces).Select(batch => ReaderTranslationProtocol.Input(from, to, batch)).ToArray();
                CollectionAssert.AreEqual(expected, entries.Values.Select(e => e.Request).ToArray());
                Assert.IsTrue(entries.Values.All(e => e.Status == "completed" && e.SystemPrompt == ReaderTranslationProtocol.Instructions &&
                    !string.IsNullOrWhiteSpace(e.Response) && e.Models.Any(m => m.Id == card.ModelArtifactId && m.Revision == card.Revision)));
                var saved = Path.Combine(folder, from + "-" + to + ".pdf"); await ReaderTranslationPdf.SaveAsync(result.Path, saved, source);
                using var pdf = PdfDocument.Open(saved); var text = pdf.GetPage(index + 1).Text;
                await File.WriteAllTextAsync(Path.Combine(folder, from + "-output.txt"), text, timeout.Token);
                Assert.AreEqual(2, pdf.NumberOfPages); StringAssert.Contains(text, "17");
                if (to == "en") StringAssert.Contains(text.ToLowerInvariant(), "meeting");
                else StringAssert.Contains(text.ToLowerInvariant(), "встреч");
                StringAssert.Contains(pdf.Information.Subject, card.ModelArtifactId); StringAssert.Contains(pdf.Information.Subject, card.Revision);
                var viewWorkspace = Path.Combine(folder, from + "-view"); Directory.CreateDirectory(viewWorkspace);
                await using var view = await ReaderPdfPages.OpenAsync(saved, viewWorkspace, timeout.Token);
                File.Copy(await view.RenderAsync(index, 1000, timeout.Token), Path.Combine(folder, from + "-" + to + ".png"), true);
                File.Copy(await document.Backend.RenderAsync(index, 1000, timeout.Token), Path.Combine(folder, from + "-original.png"), true);
                await File.WriteAllTextAsync(Path.Combine(folder, from + "-" + to + ".json"), JsonSerializer.Serialize(new {
                    model = card.ModelArtifactId, card.Revision, from, to, seconds = timer.Elapsed.TotalSeconds, text, journal = entries.Values,
                    originalsUnchanged = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))
                }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
                ApplicationBackgroundOperations.Current.Acknowledge(request.Id);
            }
            session.Dispose(); Assert.IsFalse(Directory.Exists(session.DirectoryPath));
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    private static void MakePdf(string path)
    {
        using var stream = File.Create(path); using var pdf = SKDocument.CreatePdf(stream);
        using var face = SKTypeface.FromFamilyName("Arial"); using var font = new SKFont(face, 18);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var accent = new SKPaint { Color = SKColors.CornflowerBlue };
        using var rule = new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke };
        foreach (var lines in new[] { new[] { "План встречи", "Встреча состоится 17 ноября в 18:30.", "Адрес: улица Мира, дом 12.", "Пригласите Анну. Бюджет: 100 EUR." },
            new[] { "Meeting plan", "The meeting is on November 17 at 18:30.", "Address: 12 Mira Street.", "Invite Anna. Budget: 100 EUR." } })
        {
            var canvas = pdf.BeginPage(600, 800); canvas.Clear(SKColors.White);
            canvas.DrawRect(420, 40, 120, 80, accent); canvas.DrawCircle(480, 80, 25, ink);
            canvas.DrawText(lines[0], 50, 70, font, ink);
            for (var n = 1; n < lines.Length; n++)
            { canvas.DrawRect(40, 150 + n * 55, 510, 45, rule); canvas.DrawText(lines[n], 50, 180 + n * 55, font, ink); }
            pdf.EndPage();
        }
        pdf.Close();
    }
}
