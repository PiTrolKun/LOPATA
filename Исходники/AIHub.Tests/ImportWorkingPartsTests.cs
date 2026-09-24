using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportWorkingPartsTests
{
    private string _root = "", _project = "";
    private ImportSession _session = null!;
    private ImportWorkingPartsPreparation Service => new(_session);
    private static readonly IProgress<ImportWorkingPartsProgress> Quiet = new InlineProgress<ImportWorkingPartsProgress>(_ => { });

    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "lopata-working-parts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "private.json"); File.WriteAllText(source, "[]");
        _session = ImportSession.Create(_root, source, default); _session.State.SelectedProject = "Book";
        var input = new ImportInput([new("c", "chat", 1)], [new("u", "c", "m", "", "RESPONSE", 0, 0, "OLD PRIVATE TEXT", false)], [], []);
        _project = ImportProjectBuilder.Build(_session, input, [new("u", "MAIN", "Book", "Chapter", "")],
            new LiteraryProjectStore(Path.Combine(_root, "projects.json")), _root, "Book", "test", "en").ProjectPath;
        SetBook(new string('a', 16000) + "😀е\u0301 END");
        _session.State.Stage = "book-confirmed"; _session.State.RagStatus = "ready"; _session.Save();
        MakeRag();
    }
    [TestCleanup] public void Cleanup()
    {
        _session.Dispose();
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-working-parts-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
    }
    private ImportReviewBook Book() => new ImportReviewBookStore(_project).Load();
    private void SetBook(string text)
    {
        var store = new ImportReviewBookStore(_project); var b = store.Load(); b.ReplaceText(text); b.Headings.Clear(); store.Save(b);
    }
    private void MakeRag()
    {
        var book = Book(); var revision = ImportSession.Hash(book.Text);
        var folder = Path.Combine(_project, "Rag/ImportIndexes/Book", revision); Directory.CreateDirectory(folder);
        var input = Path.Combine(folder, "text.json"); var output = Path.Combine(folder, "vectors.jsonl");
        File.WriteAllText(input, JsonSerializer.Serialize(new[] { new ImportRagSection(book.ProjectId, "book", book.Text, "project") }));
        var scalar = book.Text.EnumerateRunes().ToArray(); var vector = new float[1024]; vector[0] = 1;
        var lines = new List<string>();
        for (var i = 0; i < scalar.Length; i += 1900)
        {
            var text = string.Concat(scalar.Skip(i).Take(2000).Select(r => r.ToString()));
            lines.Add(JsonSerializer.Serialize(new { id = lines.Count + 1, vector,
                payload = new { source = book.ProjectId, section = "book", offset = i, text, kind = "project" } }));
        }
        File.WriteAllLines(output, lines);
        var manifest = new ImportRagManifest("Book", revision, new string('b', 32), lines.Count,
            GigaEmbeddingInstallation.Revision, ImportSession.HashFile(input), ImportSession.HashFile(output), book.Text.Length);
        Directory.CreateDirectory(Path.Combine(_project, "Rag/Book"));
        File.WriteAllText(Path.Combine(_project, "Rag/Book/manifest.json"), JsonSerializer.Serialize(manifest));
    }
    [TestMethod] public async Task AutomaticKeepsEveryCharacterAndOriginalArtifacts()
    {
        var before = Directory.GetFiles(_project, "*", SearchOption.AllDirectories).ToDictionary(p => p, ImportSession.HashFile);
        var result = await Service.PrepareAsync("auto", Quiet, default); Assert.IsTrue(result.Ready);
        var m = Service.Current()!; Assert.IsNotNull(m); Assert.IsTrue(result.Plan!.Warnings > 0);
        var combined = string.Concat(m.Parts.Select(p => File.ReadAllText(Path.Combine(_project, "WorkingParts", m.Generation, p.Id + ".txt"))));
        Assert.AreEqual(Book().Text, combined);
        foreach (var (path, hash) in before) Assert.AreEqual(hash, ImportSession.HashFile(path), path);
        Assert.AreEqual("book-confirmed", _session.State.Stage); Assert.AreEqual("pending", _session.State.MemoryStatus);
    }
    [TestMethod] public async Task ManualDraftResumesMovedBoundaryAndSelectionBeforePublishing()
    {
        var first = await Service.PrepareAsync("manual", Quiet, default); Assert.IsFalse(first.Ready);
        Assert.IsNull(Service.Current()); var plan = first.Plan!;
        // Moving the second boundary left keeps both adjacent parts within capacity.
        plan.MoveBoundary(Book(), 1, 7000); plan.Selected = 1; Service.SaveDraft(plan);
        var resumed = await Service.PrepareAsync("manual", Quiet, default);
        Assert.AreEqual(7000, resumed.Plan!.Parts[1].Length); Assert.AreEqual(1, resumed.Plan.Selected);
        Assert.IsTrue((await Service.CommitAsync(resumed.Plan, Quiet, default)).Ready);
        Assert.AreEqual("manual", Service.Current()!.Mode);
    }
    [TestMethod] public void HeadingsAndGraphemesArePreserved()
    {
        var book = new ImportReviewBook { ProjectId = "book", Text = "One\n" + new string('a', 7494) + "😀е\u0301\nTwo\n" + new string('b', 9000) };
        var second = book.Text.IndexOf("Two", StringComparison.Ordinal);
        book.Headings = [new("one", 0, 3), new("two", second, 3)];
        var p = ImportWorkingPartsPlan.Create(book, "auto");
        Assert.AreEqual(book.Text, string.Concat(p.Parts.Select(x => book.Text.Substring(x.Start, x.Length))));
        Assert.IsTrue(p.Parts.Any(x => x.Start == second && x.Title == "Two"));
        var boundaries = StringInfo.ParseCombiningCharacters(book.Text).Append(book.Text.Length).ToHashSet();
        Assert.IsTrue(p.Parts.All(x => x.Length <= 7500 && boundaries.Contains(x.Start + x.Length)));
    }
    [TestMethod] public void InvalidBoundaryCannotLoseTextOrSplitUnicode()
    {
        var b = new ImportReviewBook { ProjectId = "p", Text = new string('a', 7498) + "😀" + new string('b', 1000) };
        var p = ImportWorkingPartsPlan.Create(b, "manual"); var before = JsonSerializer.Serialize(p);
        Assert.ThrowsExactly<InvalidDataException>(() => p.MoveBoundary(b, 0, 7499));
        Assert.ThrowsExactly<InvalidDataException>(() => p.MoveBoundary(b, 0, 0));
        Assert.ThrowsExactly<InvalidDataException>(() => p.MoveBoundary(b, 0, 7501));
        Assert.AreEqual(before, JsonSerializer.Serialize(p));
    }
    [TestMethod] public async Task CrossingRagPointsMapExactlyAndActivePartIsExcluded()
    {
        await Service.PrepareAsync("auto", Quiet, default); var m = Service.Current()!;
        var links = JsonSerializer.Deserialize<List<ImportWorkingRagLink>>(File.ReadAllText(Path.Combine(_project, "WorkingParts", m.Generation, "rag-links.json")))!;
        var crossing = links.GroupBy(l => l.PointId).First(g => g.Select(l => l.PartId).Distinct().Count() > 1);
        var active = crossing.First().PartId;
        var visible = ImportWorkingPartsRag.Visible(links, crossing.Key, active);
        Assert.IsTrue(visible.Count > 0); Assert.IsTrue(visible.All(l => l.PartId != active));
        foreach (var link in links)
        {
            var part = m.Parts.Single(p => p.Id == link.PartId);
            Assert.AreEqual(part.Start + link.PartStart, link.BookStart);
            Assert.IsTrue(link.PartStart >= 0 && link.PartStart + link.Length <= part.Length);
        }
    }
    [TestMethod] public async Task CancelledGenerationIsNotPublishedAndCanResume()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress<ImportWorkingPartsProgress>(p => { if (p.Stage == "Writing" && p.Done == 1) cancel.Cancel(); });
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Service.PrepareAsync("auto", progress, cancel.Token));
        Assert.IsNull(Service.Current());
        Assert.IsTrue((await Service.PrepareAsync("auto", Quiet, default)).Ready);
    }
    [TestMethod] public async Task FailedWriteRetriesThreeTimesAndReportContainsNoBook()
    {
        Directory.CreateDirectory(Path.Combine(_project, "WorkingParts"));
        var target = Path.Combine(_project, "WorkingParts/manifest.json"); File.WriteAllText(target, "locked");
        using var lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        var progress = new List<ImportWorkingPartsProgress>();
        var result = await Service.PrepareAsync("auto", new InlineProgress<ImportWorkingPartsProgress>(progress.Add), default);
        Assert.IsFalse(result.Ready); Assert.IsNotNull(result.Report);
        Assert.AreEqual(4, progress.Max(p => p.Attempt)); Assert.HasCount(3, progress.Where(p => p.Stage == "Retry").ToArray());
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(result.Report)!));
        var report = File.ReadAllText(result.Report); Assert.IsFalse(report.Contains(_project)); Assert.IsFalse(report.Contains("OLD PRIVATE TEXT"));
        using var parsed = JsonDocument.Parse(report); Assert.AreEqual(4, parsed.RootElement.GetProperty("attempts").GetArrayLength());
    }
    [TestMethod] public async Task FourthAttemptCanSucceed()
    {
        Directory.CreateDirectory(Path.Combine(_project, "WorkingParts"));
        var target = Path.Combine(_project, "WorkingParts/manifest.json"); File.WriteAllText(target, "locked");
        using var lease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);
        var progress = new InlineProgress<ImportWorkingPartsProgress>(p => { if (p.Stage == "Retry" && p.Attempt == 4) lease.Dispose(); });
        Assert.IsTrue((await Service.PrepareAsync("auto", progress, default)).Ready); Assert.IsNotNull(Service.Current());
    }
    [TestMethod] public async Task StaleRagBlocksPublicationAndOldGenerationRemains()
    {
        await Service.PrepareAsync("auto", Quiet, default);
        var target = Path.Combine(_project, "WorkingParts/manifest.json"); var before = File.ReadAllText(target);
        SetBook(Book().Text + " CHANGED");
        var result = await Service.PrepareAsync("auto", Quiet, default);
        Assert.IsFalse(result.Ready); Assert.IsNotNull(result.Report); Assert.AreEqual(before, File.ReadAllText(target)); Assert.IsNull(Service.Current());
    }
    [TestMethod] public async Task ModifiedWorkingFileIsDetectedAndNeverOverwritten()
    {
        await Service.PrepareAsync("auto", Quiet, default); var m = Service.Current()!;
        var file = Path.Combine(_project, "WorkingParts", m.Generation, m.Parts[0].Id + ".txt"); File.WriteAllText(file, "User edit");
        Assert.ThrowsExactly<InvalidDataException>(() => Service.Current());
        var result = await Service.PrepareAsync("auto", Quiet, default);
        Assert.IsFalse(result.Ready); Assert.AreEqual("User edit", File.ReadAllText(file));
    }
    [TestMethod] public async Task RepeatedRunDoesNotRewriteWorkingText()
    {
        await Service.PrepareAsync("auto", Quiet, default); var m = Service.Current()!;
        var folder = Path.Combine(_project, "WorkingParts", m.Generation);
        var times = Directory.GetFiles(folder).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        Assert.IsTrue((await Service.PrepareAsync("auto", Quiet, default)).Ready);
        foreach (var (path, time) in times) Assert.AreEqual(time, File.GetLastWriteTimeUtc(path));
    }
}
