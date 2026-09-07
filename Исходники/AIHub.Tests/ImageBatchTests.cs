using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageBatchTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "lopata-batch-test-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ImageBatchJob Job(int count, bool single = true)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source.dat"); File.WriteAllText(path, "fixture");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return new() { SingleDocument = single, Items = Enumerable.Range(0, count).Select(i => new ImageBatchItem
            { File = new() { SourcePath = path, DisplayName = "same-name.jpg", Sha256 = hash } }).ToList() };
    }
    [TestMethod]
    public async Task HundredFiles_SplitByAdmission_PreserveOrderAndValidDocx()
    {
        var job = Job(100); var store = new ImageBatchStore(_root); var fake = new Fake { MaxGroup = 2 };
        var events = new List<ImageBatchProgress>();
        await new ImageBatchProcessor(store, fake).RunAsync(job, new Sink(events), default);
        Assert.AreEqual(100, fake.Analyses); Assert.AreEqual("completed", job.Status);
        Assert.IsTrue(fake.Formats.Any(g => g.Count > 1)); Assert.IsTrue(fake.Splits > 0);
        using var doc = WordprocessingDocument.Open(Path.Combine(store.Results(job), "Descriptions.docx"), false);
        var errors = new OpenXmlValidator().Validate(doc).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.Description)));
        var headings = doc.MainDocumentPart!.Document!.Descendants<BookmarkStart>().Select(b => b.Name!.Value).ToArray();
        Assert.AreEqual(100, headings.Length);
        var links = doc.MainDocumentPart!.Document.Descendants<Hyperlink>().Select(l => l.Anchor!.Value).ToArray();
        CollectionAssert.AreEqual(headings, links);
        Assert.IsTrue(events.All(p => p.Finished <= p.Total)); Assert.AreEqual("completed", events[^1].Stage);
    }
    [TestMethod]
    public async Task ThreeFailures_Continue_ResumeDoesNotDuplicateMarkdown()
    {
        var job = Job(3, false); var store = new ImageBatchStore(_root); var fake = new Fake { FailFirst = 3 };
        await new ImageBatchProcessor(store, fake).RunAsync(job, null, default);
        Assert.AreEqual(3, job.Items[0].Attempts); Assert.AreEqual("error", job.Items[0].Status);
        Assert.AreEqual(2, Directory.GetFiles(store.Results(job), "*.md").Length);
        fake.FailFirst = 0;
        await new ImageBatchProcessor(store, fake).RunAsync(job, null, default);
        Assert.AreEqual(6, fake.Analyses); // 3 failed + 2 successful + 1 retried; two analyses reused.
        Assert.AreEqual(3, Directory.GetFiles(store.Results(job), "*.md").Length);
        Assert.IsTrue(job.Items.All(i => i.Status == "ready"));
    }
    [TestMethod]
    public async Task Cancellation_PersistsCheckpoint_ResumeWithoutSourceForAnalyzedImage()
    {
        var job = Job(2); var store = new ImageBatchStore(_root); using var cts = new CancellationTokenSource();
        var fake = new Fake { AfterAnalyze = count => { if (count == 1) cts.Cancel(); } };
        try { await new ImageBatchProcessor(store, fake).RunAsync(job, null, cts.Token); Assert.Fail(); } catch (OperationCanceledException) { }
        var loaded = store.LoadAll().Single(); Assert.AreEqual("paused", loaded.Status);
        loaded.Items[0].File.SourcePath = Path.Combine(_root, "removed-temporary.png");
        fake.AfterAnalyze = null;
        await new ImageBatchProcessor(store, fake).RunAsync(loaded, null, default);
        Assert.AreEqual(2, fake.Analyses); Assert.AreEqual("completed", loaded.Status);
    }
    [TestMethod]
    public async Task ChangedSource_IsRejected_AndNoSuccessIsReported()
    {
        var job = Job(1); File.AppendAllText(job.Items[0].File.SourcePath, "changed"); var fake = new Fake();
        try { await new ImageBatchProcessor(new(_root), fake).RunAsync(job, null, default); Assert.Fail(); } catch (InvalidDataException) { }
        Assert.AreEqual(0, fake.Analyses); Assert.AreEqual("failed", job.Status); Assert.AreEqual("error", job.Items[0].Status);
    }
    [TestMethod]
    public async Task FinalFileWriteFailure_DoesNotLoseModelResults()
    {
        var job = Job(2); var store = new ImageBatchStore(_root); var fake = new Fake();
        Directory.CreateDirectory(Path.Combine(store.Results(job), "Descriptions.docx"));
        try { await new ImageBatchProcessor(store, fake).RunAsync(job, null, default); Assert.Fail(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.AreEqual("failed", job.Status);
        Directory.Delete(Path.Combine(store.Results(job), "Descriptions.docx"));
        await new ImageBatchProcessor(store, fake).RunAsync(job, null, default);
        Assert.AreEqual(2, fake.Analyses); Assert.AreEqual(1, fake.Formats.Count); Assert.AreEqual("completed", job.Status);
    }
    [TestMethod]
    public async Task TruncatedGeneration_RestartsAndRetries_UnlikeInputAdmission()
    {
        var job = Job(1); var fake = new Fake { TruncateFirst = 1 };
        await new ImageBatchProcessor(new(_root), fake).RunAsync(job, null, default);
        Assert.AreEqual(2, fake.Analyses); Assert.AreEqual(1, fake.Restarts);
        Assert.AreEqual("ready", job.Items[0].Status);
    }
    [TestMethod]
    public async Task WrongLanguage_RetriesBeforeSaving_AndRevalidatesSavedMaterial()
    {
        var job = Job(1, false); var store = new ImageBatchStore(_root);
        var fake = new Fake { EnglishFirst = 1 };
        await new ImageBatchProcessor(store, fake).RunAsync(job, null, default);
        Assert.AreEqual(2, fake.Formats.Count); Assert.AreEqual(1, fake.Restarts);
        store.SaveMaterial(job, job.Items[0].Id, "final", new ImageBatchSection(job.Items[0].Id, "Old", [English]));
        await new ImageBatchProcessor(store, fake).RunAsync(job, null, default);
        Assert.AreEqual(1, fake.Analyses); Assert.AreEqual(3, fake.Formats.Count);
        Assert.AreEqual("ready", job.Items[0].Status);
    }
    private const string English = "The landscape shows a dark forest and a distant castle under the pale moonlight.";
    [TestMethod]
    public void LanguageGuard_AllowsNamesAndQuotes_ButRejectsEnglishNarrative()
    {
        Assert.IsFalse(ImageBatchLanguageGuard.Matches(new("1", "Title", [English]), "ru"));
        Assert.IsTrue(ImageBatchLanguageGuard.Matches(new("1", "Title", [English]), "en"));
        var russian = "На экране Guild House видна надпись «Choose your path and follow the road to the castle». Вокруг неё расположены кнопки и цветные значки.";
        Assert.IsTrue(ImageBatchLanguageGuard.Matches(new("1", "Название", [russian]), "ru"));
        Assert.IsFalse(ImageBatchLanguageGuard.Matches(new("1", "Название", [russian, English]), "ru"));
    }
    private sealed class Sink(List<ImageBatchProgress> values) : IProgress<ImageBatchProgress> { public void Report(ImageBatchProgress p) => values.Add(p); }
    private sealed class Fake : IImageBatchModel
    {
        public int Analyses, FailFirst, Splits, MaxGroup = 4, TruncateFirst, Restarts, EnglishFirst;
        public Action<int>? AfterAnalyze;
        public List<IReadOnlyList<(string Id, ImageBatchAnalysis Analysis)>> Formats = [];
        public void Restart() { Restarts++; }
        public Task<ImageBatchAnalysis> AnalyzeAsync(ImageAnalysisFilePassport file, ImageAnalysisLiterarySettings settings, CancellationToken token)
        {
            Analyses++; if (Analyses <= FailFirst) throw new InvalidDataException("bad model JSON");
            if (Analyses <= TruncateFirst) throw new ImageAnalysisContextExhaustedException("output length", true);
            AfterAnalyze?.Invoke(Analyses); return Task.FromResult(new ImageBatchAnalysis("Facts " + Analyses, "Summary " + Analyses));
        }
        public Task<IReadOnlyList<ImageBatchSection>> FormatAsync(IReadOnlyList<(string Id, ImageBatchAnalysis Analysis)> items, ImageAnalysisLiterarySettings settings, CancellationToken token)
        {
            if (items.Count > MaxGroup) { Splits++; throw new ImageAnalysisContextExhaustedException("fixture context limit"); }
            Formats.Add(items);
            return Task.FromResult<IReadOnlyList<ImageBatchSection>>(items.Select(i => new ImageBatchSection(i.Id, "Описание " + i.Id, [Formats.Count <= EnglishFirst ? English : i.Analysis.Details])).ToArray());
        }
    }
}
