using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Media;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

/// <summary>Explicit real-model production orchestration. Never downloads or changes the user's model cards.</summary>
[TestClass, DoNotParallelize]
public sealed class BookSmartModelNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod, TestCategory("Native")]
    public async Task ActualCoreClassifiesDigitalTextWithoutOcrOrVisionAndPreservesAuthorWords()
        => await RunAsync("digital", "alpha");

    [TestMethod, DataRow("paddle"), DataRow("ovis"), TestCategory("Native")]
    public async Task ActualOcrVisionAndCoreReachReviewedExportWithFactualModels(string choice)
        => await RunAsync(choice, Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_NATIVE_VISION") ?? "alpha");

    private async Task RunAsync(string choice, string vision)
    {
        if (Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_MODEL_NATIVE") != "1")
            Assert.Inconclusive("Explicit LOPATA_BOOK_SMART_MODEL_NATIVE=1 is required; uses existing local models and prepared OCR runtimes.");
        Assert.IsTrue(vision is "alpha" or "beta");
        var project = AppDataPaths.ProjectRoot;
        Assert.IsNotNull(project, "Run the project native stand, not a published user installation.");
        var folder = Path.Combine(Environment.GetEnvironmentVariable("LOPATA_BOOK_SMART_NATIVE_OUTPUT")
            ?? Path.Combine(project, "Runtime", "BookSmartStand", "native"), choice + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var selection = new BookSmartSelection(choice == "digital" ? "paddle" : choice, vision);
        var actual = new ManagedModelLibraryStore();
        var core = actual.Load(ManagedModelCatalog.CoreArtifactId);
        Assert.IsNotNull(core, "An installed core model is required; no native test downloads it.");
        var corePath = await VerifyAsync(core, "main_model");
        var store = new ManagedModelLibraryStore(Path.Combine(folder, "model-library"));
        var registrationSnapshots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var actualEntries = Path.Combine(AppDataPaths.ManagedModelLibraryDirectory, "Entries");
        foreach (var path in Directory.GetFiles(actualEntries, "*.json")) registrationSnapshots[path] = SHA256.HashData(await File.ReadAllBytesAsync(path));
        BookSmartPage? recognized = null;
        var visionProfile = vision == "beta" ? OmniLlamaProfile.Beta : OmniLlamaProfile.Alpha;
        var modelsRoot = Path.Combine(folder, "models");
        if (choice != "digital")
        {
            Assert.IsTrue(File.Exists(BookSmartOcrRuntime.Python(selection)), "First run BookSmartRuntimeNativeTests for the selected runtime.");
            var visual = actual.Load(visionProfile.ArtifactId);
            Assert.IsNotNull(visual, "The selected visual model must already be installed.");
            await VerifyAsync(visual, "main_model"); await VerifyAsync(visual, "projector");
            store.Upsert(Clone(visual));
            foreach (var card in BookSmartOcrCatalog.CreateCards(selection, modelsRoot))
            {
                var stand = Path.Combine(project, "Runtime", "BookSmartStand");
                card.InstallDirectory = card.ModelArtifactId switch
                {
                    BookSmartOcrCatalog.PaddleId => Path.Combine(stand, "PaddlePaddle--PaddleOCR-VL-1.6-GGUF"),
                    BookSmartOcrCatalog.OvisId => Path.Combine(stand, "bartowski--ATH-MaaS_OvisOCR2-GGUF"),
                    BookSmartOcrCatalog.LayoutId => Path.Combine(stand, "paddle-cache", "official_models", "PP-DocLayoutV3"),
                    _ => throw new InvalidOperationException("Unexpected OCR artifact")
                };
                foreach (var file in card.Files) await VerifyFileAsync(card, file);
                card.Status = ManagedModelStatuses.Installed; card.LastVerifiedAt = DateTimeOffset.UtcNow;
                store.Upsert(card);
                if (card.ModelArtifactId == BookSmartOcrCatalog.LayoutId)
                {
                    var destination = BookSmartOcrCatalog.LayoutDirectory(modelsRoot);
                    Directory.CreateDirectory(destination);
                    foreach (var file in card.Files)
                        Assert.IsTrue(CreateHardLink(Path.Combine(destination, file.RelativePath), Path.Combine(card.InstallDirectory, file.RelativePath), IntPtr.Zero),
                            "Stand layout hard-link failed: " + Marshal.GetLastWin32Error());
                }
            }
        }
        var source = Path.Combine(folder, choice == "digital" ? "author.html" : "page.png");
        const string authorFirst = "Точный авторский текст: ёж, ъ, 3,14 и «кавычки».";
        const string authorSecond = "Вторая строка остаётся дословной: никаких придуманных дополнений.";
        if (choice == "digital") await File.WriteAllTextAsync(source,
            "<html><head><title>Native structure fixture</title></head><body><h1>Глава первая</h1><p>" + authorFirst + "</p><p>" + authorSecond + "</p></body></html>");
        else File.Copy(Path.Combine(project, "Тесты", "BookSmartStand", "russian-two-columns.png"), source);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var log = new List<string>();
        var previousLicenseGate = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (ids, token) =>
        {
            token.ThrowIfCancellationRequested(); TestContext.WriteLine("Native stand accepted existing component IDs: " + string.Join(", ", ids));
            return Task.CompletedTask; // Explicit opt-in only; does not write user's license receipts.
        };
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
            using var models = new BookSmartModels(selection, store, new UserContextService(new(), new()), corePath,
                async (endpoint, image, token) => recognized = await BookSmartOcrWorker.RecognizeAsync(selection, modelsRoot, endpoint, image, token),
                line => { lock (log) log.Add(line); TestContext.WriteLine(line); });
            using var processor = new BookSmartProcessor(models, Path.Combine(folder, "checkpoints"));
            var reviewed = false; byte[]? staged = null; BookReviewEvidence? evidence = null;
            var converter = new BookConversionService(async (session, token) =>
            {
                token.ThrowIfCancellationRequested(); reviewed = true; evidence = session.Evidence;
                Assert.IsTrue(session.CanSave); Assert.IsTrue(session.IsEvidenceCurrent);
                Assert.IsTrue(File.Exists(session.PreviewPath));
                Assert.IsTrue(evidence.Sources.Contains(source)); Assert.IsTrue(evidence.Marks.Length > 0);
                var targetText = string.Join("\n", session.Document.Blocks.Select(block => block.Text));
                var resolution = BookReviewEvidenceResolver.Resolve(targetText, evidence, session.IsEvidenceCurrent);
                Assert.IsTrue(resolution.Marks.Length > 0, "Actual converted target must contain an anchored AI review mark.");
                await OnSta(() =>
                {
                    var flow = BookReviewFlowRenderer.Create(session.Document, key => key, evidence, session.IsEvidenceCurrent, out _);
                    Assert.IsTrue(Runs(flow.Blocks).Any(run => run.Background is SolidColorBrush brush
                        && brush.Color is var color && (color == (Color)ColorConverter.ConvertFromString("#D1FAE5")
                        || color == (Color)ColorConverter.ConvertFromString("#FEE2E2"))), "Native result must reach the presentation-only color layer.");
                });
                staged = await File.ReadAllBytesAsync(session.PreviewPath, token);
                return BookReviewDecision.Save;
            }, processor);
            var result = await converter.ConvertAsync(new() { Sources = [source], OutputFormat = "html", OutputDirectory = Path.Combine(folder, "export"), Smart = selection },
                cancellationToken: deadline.Token);
            Assert.AreEqual(0, result.Failures.Length, string.Join("; ", result.Failures.Select(failure => failure.Message)));
            Assert.AreEqual(1, result.Outputs.Length); Assert.IsTrue(reviewed); Assert.IsNotNull(staged); Assert.IsNotNull(evidence);
            var output = result.Outputs[0]; CollectionAssert.AreEqual(staged, await File.ReadAllBytesAsync(output));
            var finalHtml = await File.ReadAllTextAsync(output); var document = BookMarkup.Parse(finalHtml);
            var text = document.Body!.TextContent;
            if (choice == "digital") { StringAssert.Contains(text, authorFirst); StringAssert.Contains(text, authorSecond); Assert.IsNull(recognized); }
            else
            {
                Assert.IsNotNull(recognized); Assert.IsTrue(recognized.Blocks.Length > 0);
                foreach (var block in recognized.Blocks.Where(block => !string.IsNullOrWhiteSpace(block.Text)))
                    StringAssert.Contains(WithoutWhitespace(text), WithoutWhitespace(block.Text), "Core classification must not rewrite OCR text.");
                Assert.IsTrue(document.QuerySelectorAll("img").Length > 0, "Complete original scan must remain present.");
            }
            foreach (var color in new[] { "#D1FAE5", "#FEE2E2", "#065F46", "#991B1B" })
                Assert.IsFalse(finalHtml.Contains(color, StringComparison.OrdinalIgnoreCase), "Review colors must not enter the final export.");
            var metadata = document.Head!.QuerySelector("meta[name='LOPATA.Provenance']")?.GetAttribute("content");
            Assert.IsNotNull(metadata); var provenance = JsonSerializer.Deserialize<OutputProvenance>(metadata)!;
            Assert.IsTrue(provenance.Models.Contains(models.StructureIdentity));
            if (choice == "digital") Assert.AreEqual(1, provenance.Models.Length);
            else
            {
                Assert.IsTrue(provenance.Models.Contains(models.OcrIdentity)); Assert.IsTrue(provenance.Models.Contains(models.VisionIdentity));
                foreach (var dependency in models.OcrDependencies) Assert.IsTrue(provenance.Models.Contains(dependency));
            }
            CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
            await File.WriteAllTextAsync(Path.Combine(folder, "native-result.json"), JsonSerializer.Serialize(new
            { choice, vision, output, provenance.Models, Marks = evidence.Marks.Length, OcrBlocks = recognized?.Blocks.Length, Warnings = result.Warnings }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.WriteLine("Native result: " + folder);
        }
        finally
        {
            ComponentLicenseGate.ConfirmAsync = previousLicenseGate;
            await ApplicationBackgroundOperations.RetireModelsAsync();
            await File.WriteAllLinesAsync(Path.Combine(folder, "runtime.log"), log);
            foreach (var (path, hash) in registrationSnapshots) CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)), "User model registration changed: " + Path.GetFileName(path));
        }
    }

    private static ManagedModelArtifactCard Clone(ManagedModelArtifactCard card)
        => JsonSerializer.Deserialize<ManagedModelArtifactCard>(JsonSerializer.Serialize(card))!;
    private static async Task<string> VerifyAsync(ManagedModelArtifactCard card, string purpose)
    {
        Assert.AreEqual(ManagedModelStatuses.Installed, card.Status);
        return await VerifyFileAsync(card, card.Files.Single(file => file.Purpose == purpose));
    }
    private static async Task<string> VerifyFileAsync(ManagedModelArtifactCard card, ManagedModelArtifactFile file)
    {
        var path = Path.Combine(card.InstallDirectory, file.RelativePath);
        Assert.IsTrue(File.Exists(path), "Missing prepared artifact: " + path); Assert.AreEqual(file.SizeBytes, new FileInfo(path).Length);
        await using var stream = File.OpenRead(path);
        Assert.AreEqual(file.Sha256.ToUpperInvariant(), Convert.ToHexString(await SHA256.HashDataAsync(stream)), "Artifact SHA mismatch");
        return path;
    }
    private static string WithoutWhitespace(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static IEnumerable<Run> Runs(BlockCollection blocks)
    {
        foreach (var block in blocks)
            switch (block)
            {
                case Paragraph paragraph: foreach (var run in Runs(paragraph.Inlines)) yield return run; break;
                case Section section: foreach (var run in Runs(section.Blocks)) yield return run; break;
                case System.Windows.Documents.List list: foreach (var item in list.ListItems) foreach (var run in Runs(item.Blocks)) yield return run; break;
                case Table table: foreach (var group in table.RowGroups) foreach (var row in group.Rows) foreach (var cell in row.Cells) foreach (var run in Runs(cell.Blocks)) yield return run; break;
            }
    }
    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Run run) yield return run;
            else if (inline is Span span) foreach (var nested in Runs(span.Inlines)) yield return nested;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
