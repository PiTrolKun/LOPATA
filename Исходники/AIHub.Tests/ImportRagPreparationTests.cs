using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportRagPreparationTests
{
    private string _root = "", _project = "";
    private ImportSession _session = null!;
    private ImportPreparationAnswers _answers = null!;
    private FakeBackend _backend = null!;
    private readonly List<ImportRagProgress> _progress = [];

    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-import-rag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "private-chat.json"); File.WriteAllText(source, "[]");
        _session = ImportSession.Create(_root, source, default); _session.State.SelectedProject = "Book";
        var input = new ImportInput([new("c", "chat", 1)], [new("u", "c", "m", "", "RESPONSE", 0, 0, "OLD TEXT", false)], [], []);
        _project = ImportProjectBuilder.Build(_session, input, [new("u", "MAIN", "Book", "Chapter", "")],
            new LiteraryProjectStore(Path.Combine(_root, "projects.json")), _root, "Book", "test", "en").ProjectPath;
        var store = new ImportReviewBookStore(_project); var book = store.Load(); book.ReplaceText("Corrected book 😀\nThe ending."); store.Save(book);
        _session.State.Stage = "book-confirmed"; _session.Save();
        _answers = new(_session.Root);
        foreach (var key in ImportPostReviewQuestions.QuestionKeys) _answers.Set(key, "accepted");
        _ = new ImportPostReviewQuestions(_answers);
        _backend = new(); _progress.Clear();
    }
    [TestCleanup] public void Cleanup()
    {
        _session.Dispose();
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-import-rag-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
    }
    private Task<ImportRagOutcome> Run(CancellationToken ct = default) => new ImportRagPreparation(_session, _answers, _backend)
        .RunAsync(new InlineProgress<ImportRagProgress>(_progress.Add), ct);
    private string Reference(string text = "Reference book")
    {
        var path = Path.Combine(_root, "reference.txt"); File.WriteAllText(path, text);
        _answers.Set("9", "yes"); _answers.Set("10", path); return path;
    }

    [TestMethod] public async Task NoReferenceIndexesOnlyCorrectedBookAndStopsBeforeChunksOrJelly()
    {
        var chapters = Directory.GetFiles(Path.Combine(_project, "chapters")).ToDictionary(p => p, File.ReadAllBytes);
        Assert.IsTrue((await Run()).Ready);
        Assert.HasCount(1, _backend.Inputs);
        Assert.AreEqual("Corrected book 😀\nThe ending.", _backend.Inputs[0][0].Text);
        Assert.AreEqual("project", _backend.Inputs[0][0].Kind);
        Assert.IsTrue(_progress.Any(p => p.Area == "Reference" && p.Stage == "Skipped"));
        Assert.AreEqual("book-confirmed", _session.State.Stage);
        Assert.AreEqual("pending", _session.State.MemoryStatus);
        Assert.AreEqual("ready", _session.State.RagStatus);
        foreach (var file in chapters) CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(file.Key));
    }
    [TestMethod] public async Task ReferenceThenBookHaveSeparateCollectionsAndSourceSnapshots()
    {
        var path = Reference();
        Assert.IsTrue((await Run()).Ready);
        Assert.HasCount(2, _backend.Inputs);
        Assert.AreEqual("reference", _backend.Inputs[0][0].Kind);
        Assert.AreEqual("project", _backend.Inputs[1][0].Kind);
        Assert.HasCount(2, _backend.Collections);
        Assert.AreEqual("Reference book", File.ReadAllText(path));
        Assert.AreEqual("Reference book", File.ReadAllText(Path.Combine(_project, "Materials", "0001_reference.txt")));
        Assert.HasCount(1, LiteraryProjectStore.ReadProject(_project).Materials);
        Assert.IsFalse(File.Exists(Path.Combine(_project, "Rag/Source/editing.json")));
    }
    [TestMethod] public async Task ResumeChecksReadyStagesWithoutEmbeddingAgain()
    {
        Reference(); Assert.IsTrue((await Run()).Ready);
        _backend.Inputs.Clear();
        Assert.IsTrue((await Run()).Ready);
        Assert.IsEmpty(_backend.Inputs); Assert.AreEqual(2, _backend.Verifications);
    }
    [TestMethod] public async Task MissingDatabaseIsRebuiltFromSameInput()
    {
        Assert.IsTrue((await Run()).Ready); _backend.Valid = false;
        Assert.IsTrue((await Run()).Ready); Assert.HasCount(2, _backend.Inputs);
    }
    [TestMethod] public async Task EditedBookInvalidatesOnlyBookIndex()
    {
        Reference(); await Run(); _backend.Inputs.Clear();
        var store = new ImportReviewBookStore(_project); var book = store.Load(); book.ReplaceText(book.Text + " Changed ending."); store.Save(book);
        Assert.IsTrue((await Run()).Ready);
        Assert.HasCount(1, _backend.Inputs); StringAssert.Contains(_backend.Inputs[0][0].Text, "Changed ending.");
    }
    [TestMethod] public async Task ThirdRetrySucceedsAndDoesNotMultiplyAttempts()
    {
        _backend.Failures = 3;
        Assert.IsTrue((await Run()).Ready); Assert.AreEqual(4, _backend.Calls);
        Assert.AreEqual(4, _progress.Max(p => p.Attempt));
    }
    [TestMethod] public async Task ExhaustionCreatesOneSanitizedReportAndPreservesReference()
    {
        Reference(); _backend.FailBook = true;
        var result = await Run(); Assert.IsFalse(result.Ready); Assert.IsNotNull(result.Report);
        Assert.AreEqual(5, _backend.Calls); // one successful reference, four book attempts
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(result.Report)!));
        var report = File.ReadAllText(result.Report);
        Assert.DoesNotContain("PRIVATE TEXT", report); Assert.DoesNotContain(_root, report);
        using var json = JsonDocument.Parse(report); Assert.AreEqual(4, json.RootElement.GetProperty("attempts").GetArrayLength());
        _backend.FailBook = false; _backend.Inputs.Clear();
        Assert.IsTrue((await Run()).Ready); Assert.HasCount(1, _backend.Inputs);
    }
    [TestMethod] public async Task CancellationDoesNotRetryOrCreateFailureReport()
    {
        using var cancel = new CancellationTokenSource();
        _backend.OnBuild = () => cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(cancel.Token));
        Assert.AreEqual(1, _backend.Calls);
        Assert.IsFalse(Directory.Exists(Path.Combine(_project, "Diagnostics/ImportReports")));
        Assert.IsFalse(File.Exists(Path.Combine(_project, "Rag/Book/manifest.json")));
        _backend.OnBuild = null; Assert.IsTrue((await Run()).Ready);
    }
    [TestMethod] public async Task MissingReferenceFailsRatherThanSilentlySkippingIt()
    {
        var path = Reference(); File.Delete(path);
        var result = await Run(); Assert.IsFalse(result.Ready); Assert.AreEqual(0, _backend.Calls);
        Assert.AreEqual(4, _progress.Max(p => p.Attempt));
    }
    [TestMethod] public async Task CopiedReferenceSurvivesLossOfExternalOriginal()
    {
        var path = Reference(); Assert.IsTrue((await Run()).Ready);
        File.Delete(path); _backend.Inputs.Clear();
        Assert.IsTrue((await Run()).Ready); Assert.IsEmpty(_backend.Inputs);
    }
    [TestMethod] public async Task ChangedExternalReferenceRebuildsOnlyReferenceAndProtectsEditedProjectCopy()
    {
        var path = Reference(); await Run(); _backend.Inputs.Clear();
        File.WriteAllText(path, "Updated reference"); Assert.IsTrue((await Run()).Ready);
        Assert.HasCount(1, _backend.Inputs); Assert.AreEqual("reference", _backend.Inputs[0][0].Kind);
        var material = Path.Combine(_project, "Materials/0001_reference.txt");
        Assert.AreEqual("Updated reference", File.ReadAllText(material));
        File.WriteAllText(material, "User edit"); File.WriteAllText(path, "Another update");
        Assert.IsFalse((await Run()).Ready); Assert.AreEqual("User edit", File.ReadAllText(material));
    }
    [TestMethod] public void TipsThresholdIsStrictAndCountsAllReferenceFiles()
    {
        Reference(new string('a', 20000));
        var layout = new LiteraryProjectLayout(_project);
        var paths = new[] { _answers.Values["10"] };
        Assert.IsFalse(ImportRagInput.Reference(layout, paths, default).ShowTips);
        var second = Path.Combine(_root, "second.txt"); File.WriteAllText(second, "b");
        Assert.IsTrue(ImportRagInput.Reference(layout, [..paths, second], default).ShowTips);
    }
    [TestMethod] public async Task UnconfirmedBookCannotStart()
    {
        _session.State.Stage = "book-review";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run()); Assert.AreEqual(0, _backend.Calls);
    }
    [TestMethod] public async Task DamagedBookDoesNotFallBackToOldParts()
    {
        File.WriteAllText(Path.Combine(_project, "Import/book-review.json"), "{bad");
        Assert.IsFalse((await Run()).Ready); Assert.IsEmpty(_backend.Inputs);
    }

    [TestMethod] public void VectorPayloadChecksUnicodeOffsetsAndCompleteCoverage()
    {
        var input = new ImportRagInput("Book", "revision", [new("book", "one", "😀 начало\nКонец", "project")], []);
        var path = Path.Combine(_root, "vectors.jsonl");
        var vector = new float[1024]; vector[0] = 1;
        string Row(int id, int offset, string text, string source = "book") => JsonSerializer.Serialize(new
        { id, vector, payload = new { source, section = "one", kind = "project", offset, text } });
        File.WriteAllLines(path, [Row(1, 0, "😀 начало"), Row(2, 9, "Конец")]);
        ImportRagArtifact.Validate(path, input, 2, default);
        File.WriteAllLines(path, [Row(1, 0, "😀 начало")]);
        Assert.Throws<InvalidDataException>(() => ImportRagArtifact.Validate(path, input, 1, default));
        File.WriteAllLines(path, [Row(1, 0, "😀 начало\nКонец", "other-book")]);
        Assert.Throws<InvalidDataException>(() => ImportRagArtifact.Validate(path, input, 1, default));
    }

    [TestMethod] public async Task ReferenceEditorTransactionIsNeverOverwritten()
    {
        Reference(); var marker = Path.Combine(_project, "Rag/Source/editing.json");
        File.WriteAllText(marker, "{\"unrelatedEditor\":true}");
        Assert.IsFalse((await Run()).Ready);
        Assert.AreEqual("{\"unrelatedEditor\":true}", File.ReadAllText(marker));
    }

    private sealed class FakeBackend : IImportRagBackend
    {
        public List<ImportRagSection[]> Inputs { get; } = [];
        public HashSet<string> Collections { get; } = [];
        public int Calls, Failures, Verifications;
        public bool FailBook, Valid = true;
        public Action? OnBuild;
        public async Task<int> BuildAsync(string id, string input, string vectors, IProgress<LiteraryPreparationProgress> progress, CancellationToken ct)
        {
            Calls++; OnBuild?.Invoke(); ct.ThrowIfCancellationRequested();
            var sections = JsonSerializer.Deserialize<ImportRagSection[]>(await File.ReadAllTextAsync(input, ct))!;
            if (Calls <= Failures || (FailBook && sections[0].Kind == "project")) throw new LiteraryEmbeddingException("PRIVATE TEXT credential=secret");
            Inputs.Add(sections); Collections.Add(id);
            var vector = new float[1024]; vector[0] = 1;
            await File.WriteAllLinesAsync(vectors, sections.Select((s, i) => JsonSerializer.Serialize(new
            { id = i + 1, vector, payload = new { source = s.Source, section = s.Section, text = s.Text, kind = s.Kind, offset = 0 } })), ct);
            progress.Report(new("Embedding", 100)); return sections.Length;
        }
        public Task<bool> VerifyAsync(string id, string vectors, int points, CancellationToken ct) { Verifications++; return Task.FromResult(Valid); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
