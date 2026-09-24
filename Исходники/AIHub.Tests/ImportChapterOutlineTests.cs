using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportChapterOutlineTests
{
    private string _root = "";
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-outline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Import"));
        Directory.CreateDirectory(Path.Combine(_root, "Exports", "Import"));
    }
    [TestCleanup] public void Cleanup()
    {
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-outline-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }
    private string Snapshot => Path.Combine(_root, "Import", "book-review.json");
    private string Docx => Path.Combine(_root, "Exports", "Import", "book.docx");
    private void Zip(string xml)
    {
        using var zip = ZipFile.Open(Docx, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
        writer.Write(xml);
    }
    [TestMethod]
    public async Task CorrectedBookWinsAndMergesAnchorsWithTextWithoutCollapsingRepeatedTitles()
    {
        const string text = "Глава №1. Начало\r\nТекст.\r\nГлава №1. Начало\nГлава вторая\nЭпилог";
        var book = new ImportReviewBook { Text = text, Headings = [new("a", 0, 16)] };
        await File.WriteAllTextAsync(Snapshot, JsonSerializer.Serialize(book));
        Zip("broken xml");
        var original = await File.ReadAllBytesAsync(Snapshot);
        var result = await ImportChapterOutline.ReadAsync(_root, default);
        Assert.IsFalse(result.Failed); Assert.HasCount(4, result.Items);
        Assert.HasCount(4, result.Items.Select(i => i.SourceId).Distinct().ToArray());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Snapshot));
        StringAssert.StartsWith(result.Items[1].Title, "Глава №1");
    }
    [TestMethod]
    public async Task DocxSupportsStyledAndExplicitHeadingsButNotOrdinaryProse()
    {
        Zip("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Arrival</w:t></w:r></w:p><w:p><w:r><w:t>Chapter 2. Departure</w:t></w:r></w:p><w:p><w:r><w:t>The chapter begins with a journey.</w:t></w:r></w:p></w:body></w:document>""");
        var before = await File.ReadAllBytesAsync(Docx);
        var result = await ImportChapterOutline.ReadAsync(_root, default);
        Assert.IsFalse(result.Failed); Assert.HasCount(2, result.Items);
        Assert.AreEqual("Arrival", result.Items[0].Title);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Docx));
    }
    [TestMethod]
    public async Task MalformedSnapshotNeverFallsBackToStaleDocxAndNeverThrows()
    {
        Zip("""<document>stale</document>""");
        foreach (var invalid in new[] { "{", "null", "{\"Text\":null}", "{\"Headings\":[null]}" })
        {
            await File.WriteAllTextAsync(Snapshot, invalid);
            var result = await ImportChapterOutline.ReadAsync(_root, default);
            Assert.IsTrue(result.Failed); Assert.HasCount(0, result.Items);
            Assert.AreEqual(invalid, await File.ReadAllTextAsync(Snapshot));
        }
    }
    [TestMethod]
    public async Task MissingCorruptAndOversizeFilesFallBackToManual()
    {
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, default)).Failed);
        await File.WriteAllTextAsync(Docx, "not a zip");
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, default)).Failed);
        using (var stream = File.Create(Snapshot)) stream.SetLength(ImportChapterOutline.MaxBytes + 1L);
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, default)).Failed);
    }
    [TestMethod]
    public async Task ZipExpansionAndDtdAreRejected()
    {
        Zip(new string('x', ImportChapterOutline.MaxBytes + 1));
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, default)).Failed);
        File.Delete(Docx);
        Zip("<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///missing'>]><x>&secret;</x>");
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, default)).Failed);
    }
    [TestMethod]
    public async Task CancellationAndHeadingLimitAreBounded()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.IsTrue((await ImportChapterOutline.ReadAsync(_root, cts.Token)).Failed);
        var book = new ImportReviewBook { Text = string.Join('\n', Enumerable.Range(1, ImportChapterOutline.MaxItems + 2).Select(i => "Глава " + i)) };
        var result = ImportChapterOutline.FromBook(book, default);
        Assert.IsTrue(result.Limited); Assert.HasCount(ImportChapterOutline.MaxItems, result.Items);
    }
    [TestMethod]
    public void LinksResolveOriginalPassagesAndFollowStableHeadingsButRejectStaleOffsets()
    {
        const string text = "Глава 1\nПервый текст.\nГлава 2\nВторой текст.";
        var book = new ImportReviewBook { Text = text, Headings = [new("stable", 0, 7)] };
        var initial = ImportChapterOutline.FromBook(book, default);
        var first = new ImportRouteRow { Title = "Переименовано в таблице", SourceId = initial.Items[0].SourceId, SourceRevision = initial.Items[0].Revision };
        var second = new ImportRouteRow { SourceId = initial.Items[1].SourceId, SourceRevision = initial.Items[1].Revision };
        StringAssert.Contains(ImportChapterOutline.LinkedText(initial, first)!, "Первый текст");
        Assert.IsFalse(ImportChapterOutline.LinkedText(initial, first)!.Contains("Второй текст"));
        book.ReplaceText("Вступление\n" + text, 0, 0, 11);
        var moved = ImportChapterOutline.FromBook(book, default);
        StringAssert.Contains(ImportChapterOutline.LinkedText(moved, first)!, "Первый текст");
        Assert.IsNull(ImportChapterOutline.LinkedText(moved, second));
        Assert.IsNull(ImportChapterOutline.LinkedText(moved, new ImportRouteRow()));
        book.Headings.Clear();
        Assert.IsNull(ImportChapterOutline.LinkedText(ImportChapterOutline.FromBook(book, default), first));
    }

    [TestMethod]
    public void TableDraftSurvivesResumeAndSkipWithoutBecomingAcceptedRoute()
    {
        var answers = new ImportPreparationAnswers(_root);
        foreach (var key in ImportPostReviewQuestions.QuestionKeys.Where(k => k != "35")) answers.Set(key, "existing");
        var questions = new ImportPostReviewQuestions(answers);
        var draft = new ImportRouteDraft { Scanned = true, Notes = "legacy", Rows = [new() { Number = 1, Title = "Глава 1", Existing = true }] };
        questions.SaveDraft(draft.Notes, draft.Serialize());
        Assert.IsFalse(answers.Values.ContainsKey("35"));
        var resumed = new ImportPostReviewQuestions(new ImportPreparationAnswers(_root));
        var restored = ImportRouteDraft.Parse(resumed.RouteDraft, resumed.Draft);
        Assert.AreEqual("legacy", restored.Notes); Assert.IsTrue(restored.Rows[0].Existing);
        resumed.Confirm("", restored.Serialize());
        var saved = new ImportPreparationAnswers(_root);
        Assert.AreEqual("", saved.Values["35"]); Assert.AreEqual("", saved.Values["post-review.route.35"]);
        Assert.AreEqual(restored.Serialize(), saved.Values["post-review.route-draft.35"]);
        resumed.Review(); resumed.Confirm(restored.Answer(false), restored.Serialize());
        saved = new ImportPreparationAnswers(_root);
        StringAssert.Contains(saved.Values["35"], "написано");
        Assert.AreEqual(restored.Serialize(), saved.Values["post-review.route.35"]);
    }
}
