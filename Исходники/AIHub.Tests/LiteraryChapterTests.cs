using System.Text;
using System.Text.Json;
using AIHub.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryChapterTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "lopata-chapter-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private LiteraryChapterStore Open() { var store = new LiteraryChapterStore(_root); store.Open(); return store; }

    [TestMethod]
    public void LegacyMigrationIsLosslessIdempotentAndDoesNotTouchProjectMetadata()
    {
        var text = new string('я', 8100) + "\n\tконец";
        File.WriteAllText(Path.Combine(_root, "working-draft.txt"), text);
        File.WriteAllText(Path.Combine(_root, "project.json"), "original metadata");
        var store = Open(); Assert.AreEqual(text, store.Load());
        Assert.AreEqual("[001] Без названия.txt", store.Active.FileName);
        store.Save("Правка");
        Assert.AreEqual("Правка", Open().Load());
        Assert.AreEqual(text, File.ReadAllText(Path.Combine(_root, "working-draft.txt")));
        Assert.AreEqual(text, File.ReadAllText(store.FilePath + ".bak"));
        Assert.AreEqual("original metadata", File.ReadAllText(Path.Combine(_root, "project.json")));
    }

    [TestMethod]
    public void AutosaveChoicesPersistPerProject()
    {
        var store = Open(); Assert.AreEqual(30, store.Index.AutosaveSeconds);
        CollectionAssert.AreEqual(new[] {10,20,30,40,50,60,90,120,150,180}, LiteraryChapterStore.AutosaveIntervals.ToArray());
        foreach (var value in LiteraryChapterStore.AutosaveIntervals) { store.SetAutosave(value); Assert.AreEqual(value, Open().Index.AutosaveSeconds); }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.SetAutosave(61));
        var other = new LiteraryChapterStore(Path.Combine(_root, "another")); other.Open(); Assert.AreEqual(30, other.Index.AutosaveSeconds);
    }

    [TestMethod]
    public void FinishAndContinuationAreNumericAndRenamePreservesIdentity()
    {
        var store = Open(); store.Save("Начало"); var firstId = store.Active.Id;
        for (int n = 2; n <= 11; n++) store.Continue(["Часть " + n]);
        var activeId = store.Active.Id;
        store.Rename("Ночь: вокзал?");
        Assert.AreEqual(activeId, store.Active.Id); Assert.AreEqual(firstId, store.Index.Parts[0].Id);
        Assert.AreEqual("[001.11] Ночь_ вокзал_.txt", store.Active.FileName);
        var snapshot = store.Snapshot(); Assert.HasCount(1, snapshot); Assert.HasCount(11, snapshot[0].Parts);
        Assert.AreEqual("Часть 10", snapshot[0].Parts[9]);
        store.Finish(); Assert.AreEqual(2, store.Active.Chapter); Assert.AreEqual(1, store.Active.Part);
        Assert.IsTrue(store.Index.Parts.Where(p => p.Chapter == 1).All(p => p.Finished));
        Assert.AreEqual(store.Active.Id, Open().Active.Id);
        Assert.HasCount(1, store.Snapshot());
        store.Save("Новая глава"); Assert.HasCount(2, store.Snapshot());
    }

    [TestMethod]
    public void FailedSavePreservesPrimaryAndPreviousBackup()
    {
        var store = Open(); store.Save("Первый"); store.Save("Второй");
        var time = store.LastSaved;
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.Save("Третий"));
        Assert.AreEqual("Второй", store.Load()); Assert.AreEqual(time, store.LastSaved);
        Assert.AreEqual("Первый", File.ReadAllText(store.FilePath + ".bak"));
        Assert.HasCount(0, Directory.GetFiles(Path.Combine(_root, "chapters"), "*.tmp"));
    }

    [TestMethod]
    public void MidBatchFailureRollsBackAndRetryDoesNotDuplicate()
    {
        var store = Open(); store.Save("Прежний текст");
        var blocker = Path.Combine(_root, "chapters", "[001.3] Без названия.txt"); Directory.CreateDirectory(blocker);
        Assert.Throws<IOException>(() => store.Continue(["Вставка первая", "Вставка вторая"]));
        Assert.AreEqual("Прежний текст", store.Load()); Assert.HasCount(1, Open().Index.Parts);
        Assert.IsFalse(File.Exists(Path.Combine(_root, "chapters", "[001.2] Без названия.txt")));
        Directory.Delete(blocker); store.Continue(["Вставка первая", "Вставка вторая"]);
        Assert.HasCount(3, Open().Index.Parts); Assert.AreEqual(3, store.Active.Part);
        Assert.AreEqual("Вставка вторая", store.Load());
    }

    [TestMethod]
    public void IndexCommitFailureDoesNotAdvanceChapter()
    {
        var store = Open(); store.Save("Не потерять");
        using (var locked = new FileStream(Path.Combine(_root, "chapters", "index.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(store.Finish);
        Assert.AreEqual(1, store.Active.Chapter); Assert.HasCount(1, Open().Index.Parts);
        store.Finish(); Assert.AreEqual(2, store.Active.Chapter);
    }

    [TestMethod]
    public void CrashBeforeCommitRollsBackOnlyJournalOwnedFiles()
    {
        var store = Open(); store.Save("Исходный");
        var folder = Path.Combine(_root, "chapters");
        var name = "[001.2] Без названия.txt";
        File.WriteAllText(Path.Combine(folder, name), "Оборванная вставка");
        File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "Чужое");
        File.WriteAllText(Path.Combine(folder, "pending.json"), JsonSerializer.Serialize(new { Transaction = "uncommitted", Writes = new Dictionary<string,string> { [name] = "Оборванная вставка" }, Retired = Array.Empty<string>() }));
        var reopened = Open(); Assert.AreEqual("Исходный", reopened.Load());
        Assert.IsFalse(File.Exists(Path.Combine(folder, name))); Assert.AreEqual("Чужое", File.ReadAllText(Path.Combine(folder, "unrelated.txt")));
    }

    [TestMethod]
    public void CorruptUtf8RequiresExplicitRecovery()
    {
        var store = Open(); store.Save("Исправный"); store.Save("Поздний");
        File.WriteAllBytes(store.FilePath, [0xff, 0xfe, 0xff]);
        Assert.Throws<DecoderFallbackException>(() => Open());
        store.RestoreBackup(); Assert.AreEqual("Исправный", Open().Load());
    }

    [TestMethod]
    public void CorruptIndexCanRestorePreviousManifest()
    {
        var store = Open(); store.Save("Сохранённый"); store.SetAutosave(60);
        File.WriteAllText(Path.Combine(_root, "chapters", "index.json"), "broken");
        Assert.Throws<JsonException>(() => Open());
        var recovery = new LiteraryChapterStore(_root); recovery.RestoreBackup(); recovery.Open();
        Assert.AreEqual("Сохранённый", recovery.Load()); Assert.AreEqual(30, recovery.Index.AutosaveSeconds);
    }

    [TestMethod]
    public void LongPasteSplitsAtNewlinesWithoutLosingAnyCharacters()
    {
        var text = new string('а', 4000) + "\r\n" + new string('б', 4000) + "\n" + new string('в', 7000);
        var parts = LiteraryChapterFiles.SplitParagraphs(text, 7500);
        Assert.HasCount(3, parts); Assert.AreEqual(text, string.Concat(parts));
        Assert.IsTrue(parts.All(p => p.Length <= 7500));
        Assert.ThrowsExactly<InvalidOperationException>(() => LiteraryChapterFiles.SplitParagraphs(new string('x', 7501), 7500));
    }

    [TestMethod]
    public void ExportPreservesLiteralTextParagraphsTabsAndNumericOrder()
    {
        var store = Open(); store.Rename("Первая"); store.Save("  # Это обычный текст\r\n— Кириллица\tи пробелы  ");
        for (var n = 2; n <= 10; n++) store.Continue(["Продолжение " + n]);
        store.Finish(); store.Rename("Вторая"); store.Save("Конец & < >");
        var output = Path.Combine(_root, "result.docx"); LiteraryDocxExporter.Export(store.Snapshot(), output);
        using var document = WordprocessingDocument.Open(output, false);
        Assert.HasCount(0, new OpenXmlValidator().Validate(document).ToArray());
        var paragraphs = document.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToArray();
        Assert.AreEqual("Первая", paragraphs[0].InnerText); Assert.AreEqual("  # Это обычный текст", paragraphs[1].InnerText);
        Assert.HasCount(1, paragraphs[2].Descendants<TabChar>().ToArray());
        var texts = paragraphs.Select(p => p.InnerText).ToList();
        Assert.IsTrue(texts.IndexOf("Продолжение 9") < texts.IndexOf("Продолжение 10"));
        Assert.AreEqual("Конец & < >", texts[^1]); Assert.HasCount(1, paragraphs.SelectMany(p => p.Descendants<PageBreakBefore>()).ToArray());
        Assert.IsFalse(texts.Any(t => t.Contains("[001")));
    }

    [TestMethod]
    public void MissingSourceFailsWholeExportAndEmptyProjectIsNotExported()
    {
        var store = Open(); Assert.HasCount(0, store.Snapshot());
        Assert.ThrowsExactly<InvalidOperationException>(() => LiteraryDocxExporter.Export(store.Snapshot(), Path.Combine(_root, "empty.docx")));
        store.Save("Первая"); var source = store.FilePath; store.Finish(); store.Save("Вторая"); File.Delete(source);
        Assert.Throws<FileNotFoundException>(() => store.Snapshot());
    }
}
