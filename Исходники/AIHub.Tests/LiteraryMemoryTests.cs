using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryMemoryTests
{
    private string _root = null!, _project = null!;
    private LiteraryProjectStore _registry = null!;
    [TestInitialize] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "AIHubTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        _registry = new(Path.Combine(_root, "registry.json"));
        _project = _registry.Create(_root, new() { ProjectName = "Project", Genres = ["fantasy"] }, []).ProjectPath;
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod] public void DialogueRestoresInputAndInterruptedAnswerWithoutMixingRoles()
    {
        var layout = new LiteraryProjectLayout(_project);
        var writer = new LiteraryDialogueStore(layout, LiteraryChatProfile.Writer);
        var state = writer.Load(); state.Input = "Не отправлено"; state.Messages.Add(new(true, "Задание"));
        state.Generating = true; state.Partial = "Начало ответа"; writer.Save(state);
        var restored = new LiteraryDialogueStore(new(_project), LiteraryChatProfile.Writer).Load();
        Assert.AreEqual("Не отправлено", restored.Input); Assert.AreEqual(2, restored.Messages.Count);
        Assert.IsFalse(restored.Messages[1].Complete); Assert.IsFalse(restored.Generating);
        Assert.AreEqual(0, new LiteraryDialogueStore(layout, LiteraryChatProfile.Advisor).Load().Messages.Count);
        restored.Messages.Clear(); writer.Save(restored); Assert.AreEqual(0, writer.Load().Messages.Count);
    }
    [TestMethod] public void DeletedProjectIsNotRecreatedByChapterOrDialogueWrites()
    {
        var chapters = new LiteraryChapterStore(_project); chapters.Open();
        var chat = new LiteraryDialogueStore(new(_project), LiteraryChatProfile.Writer); var state = chat.Load();
        Directory.Delete(_project, true);
        Assert.Throws<IOException>(() => chapters.Save("Should not resurrect"));
        Assert.Throws<IOException>(() => chat.Save(state)); Assert.IsFalse(Directory.Exists(_project));
        _registry.PruneMissing(); Assert.AreEqual(0, _registry.Load().Projects.Count);
    }
    [TestMethod] public void MovingClosedProjectKeepsDialoguesAndWorkingFilesReadable()
    {
        var chapters = new LiteraryChapterStore(_project); chapters.Open(); chapters.Save("Текущий текст");
        var chat = new LiteraryDialogueStore(new(_project), LiteraryChatProfile.Advisor); var state = chat.Load(); state.Input = "Черновик вопроса"; chat.Save(state);
        var moved = Path.Combine(_root, "Moved"); Directory.Move(_project, moved);
        var reopened = new LiteraryChapterStore(moved); reopened.Open(); Assert.AreEqual("Текущий текст", reopened.Load());
        Assert.AreEqual("Черновик вопроса", new LiteraryDialogueStore(new(moved), LiteraryChatProfile.Advisor).Load().Input);
    }
    [TestMethod] public void UnavailableDriveDoesNotRemoveRegistryEntry()
    {
        var drive = Enumerable.Range('D', 23).Select(c => ((char)c) + @":\")
            .FirstOrDefault(root => !new DriveInfo(root).IsReady);
        if (drive is null) Assert.Inconclusive("No unavailable drive for this fixture.");
        var entry = new LiteraryProjectEntry(Guid.NewGuid().ToString("N"), "Offline", drive + "OfflineProject");
        File.WriteAllText(Path.Combine(_root, "registry.json"), JsonSerializer.Serialize(new LiteraryProjectIndex { Projects = [entry], ActiveId = entry.Id }));
        _registry.PruneMissing();
        Assert.AreEqual(entry.Id, _registry.Load().ActiveId);
        Assert.AreEqual(1, _registry.Load().Projects.Count);
    }
    [TestMethod] public void ReservationUsesFinalFolderAndCannotAdoptExistingUserDirectory()
    {
        Assert.Throws<IOException>(() => new LiteraryProjectReservation(_root, "Project"));
        using var reserved = new LiteraryProjectReservation(_root, "Prepared");
        Assert.AreEqual(Path.Combine(_root, "Prepared"), reserved.Root);
        var entry = _registry.CreateReserved(reserved, new() { ProjectName = "Prepared", Genres = ["fantasy"] }, [], null);
        Assert.IsTrue(new LiteraryProjectLayout(entry.ProjectPath).Migrated);
        Assert.IsTrue(Directory.Exists(Path.Combine(entry.ProjectPath, "Rag")));
    }
    [TestMethod] [DataRow(1)] [DataRow(2)] [DataRow(3)]
    public async Task TransientEmbeddingFailuresStopAtFirstSuccess(int succeedAt)
    {
        var attempts = 0;
        await LiteraryEmbeddingRetry.RunAsync(() => ++attempts < succeedAt
            ? Task.FromException(new LiteraryEmbeddingException("injected")) : Task.CompletedTask, new Progress<LiteraryPreparationProgress>(), CancellationToken.None);
        Assert.AreEqual(succeedAt, attempts);
    }
    [TestMethod] public async Task ThreeFailuresAndPermanentStorageErrorsHaveDifferentRetryCounts()
    {
        var count = 0;
        await Assert.ThrowsAsync<LiteraryEmbeddingException>(() => LiteraryEmbeddingRetry.RunAsync(() => { count++; throw new LiteraryEmbeddingException("injected"); }, new Progress<LiteraryPreparationProgress>(), CancellationToken.None));
        Assert.AreEqual(3, count); count = 0;
        await Assert.ThrowsAsync<IOException>(() => LiteraryEmbeddingRetry.RunAsync(() => { count++; throw new IOException("disk full"); }, new Progress<LiteraryPreparationProgress>(), CancellationToken.None));
        Assert.AreEqual(1, count);
    }
    [TestMethod] public void ChangedFixedTextMakesOldManifestUnavailable()
    {
        var layout = new LiteraryProjectLayout(_project); layout.Initialize();
        var id = Guid.NewGuid().ToString("N"); var source = new LiterarySource(id, "001", "Title", "[001] Title.txt", true);
        var folder = LiteraryWorkIndex.Folder(layout, id); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new LiteraryWorkManifest(id,
            LiteraryWorkIndex.Revision("Old"), Guid.NewGuid().ToString("N"), 1, GigaEmbeddingInstallation.Revision)));
        Assert.IsNotNull(LiteraryWorkIndex.Current(layout, source, "Old")); Assert.IsNull(LiteraryWorkIndex.Current(layout, source, "Edited"));
    }
    [TestMethod] [DataRow("semantic_reference")] [DataRow("semantic_project")] [DataRow("search_reference")] [DataRow("read_reference")]
    public void ReadProtocolPreservesCorpusAndReferenceAddress(string action)
    {
        var parsed = LiteraryReadingSession.Parse(JsonSerializer.Serialize(new { action, number = "ref:2", offset = 15, query = "Графиня" }));
        Assert.AreEqual(action, parsed.Action);
        if (action == "read_reference") Assert.AreEqual("ref:2", parsed.Number); else Assert.AreEqual("Графиня", parsed.Query);
    }
}
