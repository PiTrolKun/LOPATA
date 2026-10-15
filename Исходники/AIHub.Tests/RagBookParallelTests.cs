using System.Diagnostics;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagBookParallelTests
{
    [TestMethod]
    public async Task NativeParallelOcrKeepsPageOrderAndResumesAfterCancellation()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_RAG_PARALLEL_EVIDENCE");
        if (evidence is null) { Assert.Inconclusive("Explicit native OCR benchmark requested separately."); return; }
        var root = Path.Combine(evidence, "native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previous = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => Task.CompletedTask;
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
            const int pages = 8;
            for (var i = 0; i < pages; i++)
                builder.AddPage(600, 800).AddPng(png.ToArray(), new UglyToad.PdfPig.Core.PdfRectangle(0, 0, 600, 800));
            var source = Path.Combine(root, "scanned.pdf"); File.WriteAllBytes(source, builder.Build());
            var sequential = Path.Combine(root, "sequential"); Directory.CreateDirectory(sequential);
            var reference = new List<(string Text, string[] Flags)>();
            var clock = Stopwatch.StartNew();
            for (var number = 1; number <= pages; number++) reference.Add(await RagBookOcr.ReadAsync(source, number, sequential, default));
            var sequentialSeconds = clock.Elapsed.TotalSeconds;
            var job = RagBookPreparation.Create("Parallel", root, root, [source]);
            var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
            var maximumCapacity = 0;
            var progress = new InlineProgress<RagBookProgress>(p => { if (p.Stage == "Load") maximumCapacity = Math.Max(maximumCapacity, p.Total); });
            clock.Restart();
            var result = await Task.Run(() => RagBookSourcePreparation.Prepare(job, work, progress, default).Single());
            var parallelSeconds = clock.Elapsed.TotalSeconds;
            Assert.AreEqual(pages, result.Pages.Length);
            for (var i = 0; i < pages; i++)
            {
                var page = JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(result.Pages[i]))!;
                Assert.AreEqual(i + 1, page.Page);
                Assert.AreEqual(reference[i].Text, page.Text);
                CollectionAssert.AreEqual(reference[i].Flags, page.Flags);
            }

            // A fresh target tests interruption with multiple native children, not only cache hits.
            var interrupted = job with { Directory = Path.Combine(root, "interrupted") };
            using var cancellation = new CancellationTokenSource();
            var cancelProgress = new InlineProgress<RagBookProgress>(p => { if (p.Stage == "Extract" && p.Done >= 2 && p.Total == pages) cancellation.Cancel(); });
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await Task.Run(() => RagBookSourcePreparation.Prepare(interrupted, work, cancelProgress, cancellation.Token)));
            Assert.IsFalse(OwnedProcessRegistry.Shared.GetSnapshot().Any(p => p.Component == "RagBooks.OCR"),
                "Pause/cancellation must wait for all owned page workers to exit.");
            var saved = Directory.GetFiles(interrupted.Directory, "page-*.json", SearchOption.AllDirectories)
                .Where(p => File.Exists(p + ".sha256")).ToDictionary(p => p, File.GetLastWriteTimeUtc);
            Assert.IsTrue(saved.Count >= 2);
            var resumed = await Task.Run(() => RagBookSourcePreparation.Prepare(interrupted, work, progress, default).Single());
            Assert.AreEqual(pages, resumed.Pages.Length);
            foreach (var page in saved) Assert.AreEqual(page.Value, File.GetLastWriteTimeUtc(page.Key));
            for (var i = 0; i < pages; i++)
                Assert.AreEqual(reference[i].Text, JsonSerializer.Deserialize<RagBookPage>(File.ReadAllText(resumed.Pages[i]))!.Text);
            RagBookPreparation.AtomicJson(Path.Combine(root, "benchmark.json"), new
                { pages, sequentialSeconds, parallelSeconds, speedup = sequentialSeconds / parallelSeconds, maximumCapacity,
                    preservedPages = saved.Count, equalTextAndFlags = true, resumed = true });
            Console.WriteLine($"Native OCR {pages} pages: serial {sequentialSeconds:0.00}s; parallel {parallelSeconds:0.00}s; capacity {maximumCapacity}; preserved {saved.Count}. Evidence: {root}");
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}
