using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportReviewBookTests
{
    private string _folder = "", _project = "";
    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "lopata-book-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        var source = Path.Combine(_folder, "input.json"); File.WriteAllText(source, "[]");
        using var session = ImportSession.Create(_folder, source, default); session.State.SelectedProject = "Synthetic";
        var units = new[]
        {
            new ImportUnit("a", "c", "m", "", "RESPONSE", 0, 0, new string('Я', 7600) + "😀 tail", false),
            new ImportUnit("b", "c", "m", "", "RESPONSE", 0, 7607, "QUESTION", false),
            new ImportUnit("c", "c", "n", "", "RESPONSE", 0, 0, "End\nFinal line", false)
        };
        var input = new ImportInput([new("c", "synthetic", 3)], units.ToList(), [], []);
        var decisions = units.Select((u, i) => new ImportDecision(u.Id, i == 1 ? "DOUBT" : "MAIN", "Synthetic", i < 2 ? "Chapter one" : "Chapter two", "check source")).ToArray();
        _project = ImportProjectBuilder.Build(session, input, decisions, new LiteraryProjectStore(Path.Combine(_folder, "projects.json")),
            _folder, "Book", "test", "en").ProjectPath;
    }
    [TestCleanup]
    public void Cleanup()
    {
        if (Path.GetFullPath(_folder).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-book-review-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_folder, true);
    }
    [TestMethod]
    public void ContinuousBookJoinsExactPartsAndKeepsOriginalFiles()
    {
        var files = Directory.GetFiles(Path.Combine(_project, "chapters"), "*.txt").ToDictionary(p => p, File.ReadAllText);
        var store = new ImportReviewBookStore(_project); var book = store.Load();
        Assert.HasCount(2, book.Headings);
        Assert.IsTrue(book.Text.Contains(new string('Я', 7600) + "😀 tailQUESTION"));
        var at = book.Text.IndexOf(new string('Я', 10), StringComparison.Ordinal) + 7490;
        book.ReplaceText(book.Text.Remove(at, 40).Insert(at, "Across old TXT boundary"));
        book.ReplaceText(book.Text + new string('x', 16000)); // No working-part length restriction in a book editor.
        store.Save(book);
        Assert.AreEqual(book.Text, new ImportReviewBookStore(_project).Load().Text);
        foreach (var file in files) Assert.AreEqual(file.Value, File.ReadAllText(file.Key));
    }
    [TestMethod]
    public void MarkOffsetsFollowEditsAndUndoRestoresDeletedMarks()
    {
        var book = new ImportReviewBookStore(_project).Load(); var original = book.Text;
        var mark = book.Marks.Single();
        book.ReplaceText("prefix😀" + original);
        Assert.AreEqual(mark.Start + "prefix😀".Length, book.Marks.Single().Start);
        var shifted = book.Marks.Single(); var beforeDeletion = book.Text;
        book.ReplaceText(book.Text.Remove(shifted.Start, shifted.Length)); Assert.HasCount(0, book.Marks);
        book.ReplaceText(beforeDeletion); Assert.AreEqual(shifted, book.Marks.Single());
        book.ReplaceText(original); Assert.AreEqual(mark, book.Marks.Single());
    }
    [TestMethod]
    public void EditingMarkedTextDoesNotApproveItAndAdjacentInsertionIsNotMarked()
    {
        var book = new ImportReviewBookStore(_project).Load(); var mark = book.Marks.Single();
        book.ReplaceText(book.Text.Insert(mark.Start + mark.Length, " outside"));
        Assert.AreEqual(mark, book.Marks.Single());
        book.ReplaceText(book.Text.Remove(mark.Start, mark.Length).Insert(mark.Start, "review this instead"));
        Assert.AreEqual("review this instead", book.Text.Substring(book.Marks.Single().Start, book.Marks.Single().Length));
    }
    [TestMethod]
    public void DocxUsesSavedWholeBookAndPreservesPendingHighlight()
    {
        var store = new ImportReviewBookStore(_project); var book = store.Load();
        book.ReplaceText(book.Text.Replace("Final line", "Corrected final line 😀")); store.Save(book);
        var path = Path.Combine(_folder, "book.docx"); ImportBookExporter.Export(_project, path, false, default);
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.HasCount(0, new OpenXmlValidator().Validate(doc).ToArray());
        var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Skip(1).Select(p => p.InnerText);
        Assert.AreEqual(book.Text.Replace("\r\n", "\n"), string.Join("\n", paragraphs));
        Assert.IsTrue(doc.MainDocumentPart.Document.Descendants<Highlight>().Any());
    }
    [TestMethod]
    public void ApprovedMarkPersistsAndOriginalReviewIsUnchanged()
    {
        var reviewPath = Path.Combine(_project, "Import", "review.json"); var before = File.ReadAllText(reviewPath);
        var store = new ImportReviewBookStore(_project); var book = store.Load(); book.Marks.Clear(); store.Save(book);
        Assert.HasCount(0, new ImportReviewBookStore(_project).Load().Marks);
        Assert.AreEqual(before, File.ReadAllText(reviewPath));
    }
    [TestMethod]
    public void RejectsConcurrentEditsAndChangedSourceRatherThanLosingText()
    {
        var first = new ImportReviewBookStore(_project); var a = first.Load(); first.Save(a);
        var second = new ImportReviewBookStore(_project); var b = second.Load();
        a.ReplaceText(a.Text + "first"); first.Save(a);
        b.ReplaceText(b.Text + "second"); Assert.Throws<IOException>(() => second.Save(b));
        var source = Directory.GetFiles(Path.Combine(_project, "chapters"), "*.txt").First();
        File.AppendAllText(source, "external edit");
        Assert.Throws<IOException>(() => new ImportReviewBookStore(_project).Load());
    }
    [TestMethod]
    public void EditorLeasePreventsTwoWriters()
    {
        var store = new ImportReviewBookStore(_project);
        using (store.AcquireEditor()) Assert.Throws<IOException>(() => store.AcquireEditor());
        using var reopened = store.AcquireEditor(); Assert.IsNotNull(reopened);
    }
    [TestMethod]
    public void PositionSurvivesReadingOnlyAndFallsBackToBackup()
    {
        var state = new ImportBookEditorState { SelectionStart = 7000, SelectionLength = 3, TopCharacter = 6500,
            TopPixel = -2, VerticalOffset = 1200, Search = "find me", Left = 300, Top = 200, Width = 1400,
            Height = 900, FontSize = 21, Maximized = true, ContentsVisible = false };
        state.Save(_project); Assert.AreEqual(state, ImportBookEditorState.Load(_project));
        (state with { Search = "new search" }).Save(_project);
        File.WriteAllText(Path.Combine(_project, "Import", "book-editor-state.json"), "broken");
        Assert.AreEqual(state, ImportBookEditorState.Load(_project));
        var clamped = state.Clamp(20);
        Assert.AreEqual(20, clamped.SelectionStart); Assert.AreEqual(0, clamped.SelectionLength); Assert.AreEqual(20, clamped.TopCharacter);
    }
    [TestMethod]
    public void NativeOffsetsDisambiguateDeletionAmongIdenticalPassages()
    {
        var book = new ImportReviewBook { Text = "repeat repeat repeat", Marks = [new("m", 0, 7, "source", "check")] };
        book.ReplaceText("repeat repeat", 0, 7, 0);
        Assert.HasCount(0, book.Marks);
        book.ReplaceText("repeat repeat repeat", 0, 0, 7);
        Assert.AreEqual(0, book.Marks.Single().Start);
    }
    [TestMethod]
    public void ReplacingAChapterTitleKeepsItsContentsEntry()
    {
        var book = new ImportReviewBookStore(_project).Load(); var heading = book.Headings[0];
        var next = book.Text.Remove(heading.Start, heading.Length).Insert(heading.Start, "New chapter title");
        book.ReplaceText(next, heading.Start, heading.Length, "New chapter title".Length);
        Assert.HasCount(2, book.Headings); Assert.AreEqual("New chapter title", book.HeadingTitle(book.Headings[0]));
    }
    [TestMethod]
    public void WholeBookReplacementAndUndoKeepTextAndRestoreNavigation()
    {
        var book = new ImportReviewBookStore(_project).Load(); var before = book.Text;
        var headings = book.Headings.ToArray(); var marks = book.Marks.ToArray();
        book.ReplaceText("New manuscript 😀", 0, before.Length, "New manuscript 😀".Length);
        Assert.AreEqual("New manuscript 😀", book.Text); Assert.HasCount(0, book.Headings);
        book.ReplaceText(before);
        CollectionAssert.AreEqual(headings, book.Headings.ToArray()); CollectionAssert.AreEqual(marks, book.Marks.ToArray());
    }
    [TestMethod]
    public void MissingCanonicalBookWithBackupCannotSilentlyExportOldParts()
    {
        var store = new ImportReviewBookStore(_project); var book = store.Load(); store.Save(book);
        book.ReplaceText(book.Text + "updated"); store.Save(book); File.Delete(store.FilePath);
        Assert.Throws<IOException>(() => ImportBookExporter.Export(_project, Path.Combine(_folder, "book.docx"), false, default));
    }
}
