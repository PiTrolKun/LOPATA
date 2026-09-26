using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryParagraphRevisionTests
{
    private string _root = "";
    private LiteraryProject _project = null!;
    private LiteraryChapterStore _chapters = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", "ParagraphRevision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _project = new();
        File.WriteAllText(Path.Combine(_root, "project.json"), JsonSerializer.Serialize(_project));
        new LiteraryProjectLayout(_root).Initialize();
        _chapters = new(_root); _chapters.Open();
        _chapters.Save("В истории ключ медный."); _chapters.Finish();
        _chapters.Save("Черновик на диске.");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    private LiteraryEditorSnapshot Snapshot(string text) =>
        LiteraryEditorSnapshot.Capture(_project.Id, _root, _chapters.Index, text, true);

    [TestMethod]
    public void ImportReviewChangeInvalidatesStampWithoutChangingBookText()
    {
        var snapshot = Snapshot("Текущий черновик.");
        var part = _chapters.Index.Parts[0];
        var text = File.ReadAllText(Path.Combine(_root, "chapters", part.FileName));
        var allowedBefore = ImportEligibility.Allowed(_root, part.Id, text);
        Assert.AreEqual(text, string.Concat(allowedBefore));
        var before = LiteraryParagraphRevision.Capture(snapshot);
        var folder = new LiteraryProjectLayout(_root).EnsureFolder("Import");
        var review = new ImportReviewFile(1, "test-session", [new(part.Id, LiteraryWorkIndex.Revision(text),
            [new(0, text.Length, "unit-1", "Needs review")])]);
        var path = Path.Combine(folder, "review.json");
        File.WriteAllText(path, JsonSerializer.Serialize(review, ImportJson.Options));

        Assert.IsEmpty(ImportEligibility.Allowed(_root, part.Id, text));
        var excluded = LiteraryParagraphRevision.Capture(snapshot);
        Assert.AreNotEqual(before, excluded);
        Assert.AreEqual(text, File.ReadAllText(Path.Combine(_root, "chapters", part.FileName)));

        File.WriteAllText(path, JsonSerializer.Serialize(review with { Parts = [new(part.Id,
            LiteraryWorkIndex.Revision(text), [])] }, ImportJson.Options));
        Assert.AreEqual(text, string.Concat(ImportEligibility.Allowed(_root, part.Id, text)));
        Assert.AreNotEqual(excluded, LiteraryParagraphRevision.Capture(snapshot));
    }

    [TestMethod]
    public void UnsavedEditorChangeHasOwnStampAndSnapshotRemainsImmutable()
    {
        var first = Snapshot("На экране ключ зелёный.");
        var next = Snapshot("На экране ключ красный.");
        var firstStamp = LiteraryParagraphRevision.Capture(first);

        Assert.AreNotEqual(first.Revision, next.Revision);
        Assert.AreNotEqual(firstStamp, LiteraryParagraphRevision.Capture(next));
        Assert.AreEqual(firstStamp, LiteraryParagraphRevision.Capture(first));
        Assert.AreEqual("Черновик на диске.", _chapters.Load());
    }
}
