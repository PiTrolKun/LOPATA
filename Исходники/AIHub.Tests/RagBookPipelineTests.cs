using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagBookPipelineTests
{
    [TestMethod]
    public async Task ScannedPdfUsesWindowsRendererAndExistingOcr()
    {
        if (!new ComponentManager().GetStatus().Single(s => s.Entry.Id == "runtime.tesseract").IsAvailable)
        { Assert.Inconclusive("Existing OCR component is required."); return; }
        var root = Path.Combine(Path.GetTempPath(), "rag-scan-" + Guid.NewGuid().ToString("N"));
        var previous = ComponentLicenseGate.ConfirmAsync;
        try
        {
            Directory.CreateDirectory(root); ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            using var bitmap = new System.Drawing.Bitmap(1400, 1800);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Arial", 32))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawString("Rhyme connects endings.\nPoetry uses stressed syllables.\nCaesura divides lines.",
                    font, System.Drawing.Brushes.Black, new System.Drawing.PointF(80, 120));
            }
            using var png = new MemoryStream(); bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
            builder.AddPage(600, 800).AddPng(png.ToArray(), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 600, 800));
            var path = Path.Combine(root, "scanned.pdf"); File.WriteAllBytes(path, builder.Build());
            var result = await RagBookOcr.ReadAsync(path, 1, root, default);
            if (Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_OUTPUT") is { } evidence)
            {
                var target = Path.Combine(evidence, "ocr"); Directory.CreateDirectory(target);
                foreach (var file in Directory.GetFiles(root)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            Console.WriteLine(result.Text + " FLAGS=" + string.Join(",", result.Flags));
            Assert.Contains("caesura", result.Text.ToLowerInvariant());
            // Native font rasterization can change confidence. Uncertain OCR must be rejected, even when the sentence looks right.
            var confidence = File.ReadLines(Path.Combine(root, "ocr-000001.tsv")).Skip(1).Select(line => line.Split('\t'))
                .Where(fields => fields.Length >= 12 && fields[0] == "5" && !string.IsNullOrWhiteSpace(fields[11]))
                .Select(fields => double.Parse(fields[10], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.IsTrue(confidence.Length > 0);
            CollectionAssert.AreEqual(confidence.Any(value => value < 85) ? new[] { "ocr_low_confidence" } : Array.Empty<string>(), result.Flags);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SourceSnapshotPreservesTextAndSurvivesRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var source = Path.Combine(root, "Ударе́ния.txt");
            const string original = "Рифма и ударе́ние.\r\nЁлка и строка.\n\nПример.\n";
            File.WriteAllText(source, original);
            var job = RagBookPreparation.Create("Проверка", root, root, [source]);
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            var progress = new Progress<RagBookProgress>();
            var first = RagBookSourcePreparation.Prepare(job, work, progress, default).Single();
            var time = File.GetLastWriteTimeUtc(first.Pages.Single());
            Assert.AreEqual(original, JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(first.Pages.Single()))!.Text);
            var second = RagBookSourcePreparation.Prepare(job, work, progress, default).Single();
            Assert.AreEqual(first.Sha256, second.Sha256); Assert.AreEqual(time, File.GetLastWriteTimeUtc(second.Pages.Single()));
            File.WriteAllText(second.Pages.Single(), "incomplete page");
            var repaired = RagBookSourcePreparation.Prepare(job, work, progress, default).Single();
            Assert.AreEqual(original, JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(repaired.Pages.Single()))!.Text);
            File.WriteAllText(source, "Изменённый источник. Новый текст.");
            var changed = RagBookSourcePreparation.Prepare(job, work, progress, default).Single();
            Assert.AreNotEqual(first.Sha256, changed.Sha256);
            Assert.AreEqual(original, File.ReadAllText(first.Snapshot));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RealSelectedBookBuildAndQuery()
    {
        var source = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_BOOK");
        var models = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_MODELS");
        var output = Environment.GetEnvironmentVariable("LOPATA_RAG_TEST_OUTPUT");
        if (source is null || models is null || output is null) { Assert.Inconclusive("Explicit local integration input is required."); return; }
        var previous = ComponentLicenseGate.ConfirmAsync;
        var previousController = ApplicationBackgroundOperations.Current;
        var background = new BackgroundOperationController(new(Path.Combine(output, "integration-background.json")));
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
            ApplicationBackgroundOperations.Current = background;
            var progress = new Progress<RagBookProgress>(p => Console.WriteLine($"{p.Stage} {p.Done}/{p.Total} {p.Detail}"));
            var job = RagBookPreparation.Create("Практическая проверка", output, models, [source]);
            Console.WriteLine("Library: " + job.Directory);
            await new RagBookPreparation().RunAsync(job, "RAG integration", progress, default);
            Assert.AreEqual(AIHub.Models.BackgroundOperationPhase.Completed, background.State!.Phase);
            Assert.IsTrue(File.Exists(Path.Combine(job.Directory, "current.json")));
            var hits = await RagBookWorker.SearchAsync(job.Directory, models, "Что такое цезура в стихе?", progress, default);
            using var doc = JsonDocument.Parse(hits);
            Assert.IsTrue(doc.RootElement.GetProperty("hits").GetArrayLength() > 0);
            Console.WriteLine(hits);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; ApplicationBackgroundOperations.Current = previousController; }
    }
}
