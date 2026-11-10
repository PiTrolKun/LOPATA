using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using AIHub.Models;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SpeechAssetsTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "speech-assets-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void MarkdownNestedListsKeepOwnParagraphsAndDuplicateLines()
    {
        var rows = SpeechAssetMarkdown.Parse("# Group\n\n- **Parent** [link](https://example.org)\n\n  Next paragraph.\n  - Child\n- [x] Done\n- Child\n\n```\n- not a line\n```\n");
        CollectionAssert.AreEqual(new[] { "Parent link\n\nNext paragraph.", "Child", "Done", "Child" }, rows.Select(r => r.Text).ToArray());
        Assert.IsTrue(rows.All(r => r.Group == "Group"));
        Assert.AreEqual(rows.Count, rows.Select(r => r.Id).Distinct().Count());
    }

    [TestMethod]
    public void EditsAndSelectionRequireExplicitConfirmationAndPreserveOriginal()
    {
        var row = new SpeechAssetRow { SourceText = "Елена", Text = "Елена" };
        row.Takes.Add(new() { Text = "Елена", Selected = true });
        Assert.IsFalse(row.Confirmed); row.Confirm(); Assert.IsTrue(row.Confirmed);
        row.Edit("Элена"); Assert.IsFalse(row.Confirmed); Assert.AreEqual("Елена", row.SourceText);
        Assert.Throws<InvalidDataException>(() => row.Confirm());
        row.Edit(row.SourceText); row.Confirm(); Assert.IsTrue(row.Confirmed); Assert.HasCount(1, row.Takes);
    }

    [TestMethod]
    public void ChoosingTakeExcludesOtherVersionsAndPreservesTheirFiles()
    {
        var row = new SpeechAssetRow { SourceText = "MP3", Text = "эмпэ три" };
        var old = new SpeechAssetTake { Id = Guid.NewGuid().ToString("N"), Text = "MP3", Selected = true, File = "old.wav" };
        var current = new SpeechAssetTake { Id = Guid.NewGuid().ToString("N"), Text = row.Text, Selected = true, File = "current.wav" };
        row.Takes.AddRange([old, current]);
        Assert.IsFalse(row.CanConfirm, "Legacy multiple selection requires an explicit choice.");
        row.SelectTake(current.Id); Assert.IsFalse(old.Selected); Assert.IsTrue(current.Selected);
        row.Confirm(); Assert.IsTrue(row.Confirmed);
        row.SelectTake(current.Id); Assert.IsTrue(row.Confirmed, "Re-selecting the same take is not an edit.");
        row.SelectTake(old.Id); Assert.IsFalse(row.Confirmed); Assert.IsFalse(row.CanConfirm);
        Assert.Throws<InvalidDataException>(() => row.Confirm());
        Assert.HasCount(2, row.Takes); Assert.AreEqual("old.wav", old.File); Assert.AreEqual("current.wav", current.File);
    }

    [TestMethod]
    public void ZipContainsOnlyChosenConfirmedTakesAndRejectsTamperingAndCollisions()
    {
        var store = new SpeechAssetStore(_root); var session = new SpeechAssetSession();
        var row = new SpeechAssetRow { Text = "hello", SourceText = "hello", FileName = "0001" }; session.Rows.Add(row);
        AddTake(store, session, row, true); AddTake(store, session, row, false); store.Save(session);
        var zip = Path.Combine(_root, "export.zip");
        Assert.Throws<InvalidDataException>(() => store.Export(session, zip));
        row.Confirm(); store.Save(session); session = store.Load(session.Id);
        store.Export(session, zip);
        using (var archive = ZipFile.OpenRead(zip))
        {
            CollectionAssert.AreEquivalent(new[] { "audio/0001.wav", "manifest.json" }, archive.Entries.Select(e => e.FullName).ToArray());
            using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
            Assert.AreEqual("hello", manifest.RootElement.GetProperty("Items")[0].GetProperty("SynthesisText").GetString());
        }
        File.AppendAllText(store.FileFor(session.Id, session.Rows[0].Takes[0].File), "changed");
        Assert.Throws<InvalidDataException>(() => store.Export(session, zip));
        using var intact = ZipFile.OpenRead(zip); Assert.HasCount(2, intact.Entries);
    }

    [TestMethod]
    public void SafePathsAndOutputNamesRejectEscapeAndReservedNames()
    {
        var store = new SpeechAssetStore(_root); var id = Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "../x", "CON", "LPT1.wav", "x:y", "" }) Assert.Throws<InvalidDataException>(() => SpeechAssetStore.SafeName(name));
        foreach (var name in new[] { "../../escape", "C:/escape", "file:stream" }) Assert.Throws<InvalidDataException>(() => store.FileFor(id, name));
    }

    [TestMethod]
    public async Task RealWorkPreservesTakesAndProvenanceOnRetryWithoutAutoConfirmation()
    {
        var store = new SpeechAssetStore(_root); var session = new SpeechAssetSession { Voice = new("silero56", "ru", 100, new("xenia", Device: "cpu")) };
        session.Rows = SpeechAssetMarkdown.Parse("- Первая реплика\n- Вторая реплика"); store.Save(session);
        var registry = new VideoNarratorRegistry(); var calls = 0;
        registry.Register("silero56", () => new FakeNarrator(_root, () => calls++));
        var work = new SpeechAssetWork(store, registry);
        var job = new SpeechAssetJob(session.Id, session.Voice, session.Rows.Select(r => new SpeechAssetJobItem(r.Id, Guid.NewGuid().ToString("N"), r.Text)).ToArray());
        await work.RunAsync(job, "test", null, default); await work.RunAsync(job, "test", null, default);
        Assert.AreEqual(2, calls); var loaded = store.Load(session.Id);
        Assert.IsTrue(loaded.Rows.All(r => !r.Confirmed && r.Takes.Count == 1));
        var file = store.FileFor(session.Id, loaded.Rows[0].Takes[0].File);
        using var wave = new WaveFileReader(file); Assert.AreEqual(.1, wave.TotalTime.TotalSeconds, .0001);
        StringAssert.Contains(Encoding.UTF8.GetString(File.ReadAllBytes(file)), "speech-assets/generate");
        StringAssert.Contains(Encoding.UTF8.GetString(File.ReadAllBytes(file)), "test-revision");
        Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(_root));
    }
    private static void AddTake(SpeechAssetStore store, SpeechAssetSession session, SpeechAssetRow row, bool selected)
    {
        var id = Guid.NewGuid().ToString("N"); var relative = "audio/" + id + ".wav"; var file = store.FileFor(session.Id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var writer = new WaveFileWriter(file, new(16000, 16, 1))) writer.Write(new byte[3200], 0, 3200);
        row.Takes.Add(new() { Id = id, File = relative, Text = row.Text, Selected = selected, Sha256 = SpeechAssetStore.Hash(file), Seconds = .1 });
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PauseResumeReusesCompletedRowsAndDisposesTheNarrator(bool audit)
    {
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "background.json")));
        ApplicationBackgroundOperations.Current = controller;
        using var auditStore = ComponentAudit.UseTestStore(Path.Combine(_root, "audit"), audit);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var disposed = 0;
        var store = new SpeechAssetStore(_root); var session = new SpeechAssetSession { Voice = new("silero56", "ru", 100, new("xenia")),
            Rows = SpeechAssetMarkdown.Parse("- Первая\n- Вторая") }; store.Save(session);
        var registry = new VideoNarratorRegistry();
        registry.Register("silero56", () => new BlockingNarrator(_root, async token =>
        {
            if (++calls == 2) { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        }, () => disposed++));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var work = new SpeechAssetWork(store, registry);
            var job = new SpeechAssetJob(session.Id, session.Voice, session.Rows.Select(r => new SpeechAssetJobItem(r.Id, Guid.NewGuid().ToString("N"), r.Text)).ToArray());
            var task = work.RunAsync(job, "probe", null, timeout.Token);
            await blocked.Task.WaitAsync(timeout.Token); await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.IsTrue(disposed > 0);
            await controller.ResumeAsync(timeout.Token); await task;
            Assert.AreEqual(3, calls); Assert.IsFalse(controller.BlocksNewWork);
            Assert.IsTrue(store.Load(session.Id).Rows.All(r => r.Takes.Count == 1 && !r.Confirmed));
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    private sealed class BlockingNarrator(string root, Func<CancellationToken, Task> before, Action disposed) : IVideoNarrator
    {
        public IReadOnlyList<VideoVoice> Voices => [];
        public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
        {
            await before(token); var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            using (var writer = new WaveFileWriter(file, new(16000, 16, 1))) writer.Write(new byte[3200], 0, 3200);
            return new(file, new("test", "test", "revision", "speech"));
        }
        public void Dispose() => disposed();
    }
    [TestMethod]
    public async Task FailedGenerationReleasesTheCommonGateWithoutAutomaticRetry()
    {
        var previous = ApplicationBackgroundOperations.Current;
        var controller = new BackgroundOperationController(new(Path.Combine(_root, "background.json")));
        ApplicationBackgroundOperations.Current = controller;
        try
        {
            var store = new SpeechAssetStore(_root); var session = new SpeechAssetSession { Voice = new("silero56", "ru", 100, new("xenia")),
                Rows = SpeechAssetMarkdown.Parse("- Test") }; store.Save(session);
            var registry = new VideoNarratorRegistry(); var calls = 0;
            registry.Register("silero56", () => { calls++; throw new IOException("worker failure"); });
            var row = session.Rows.Single();
            await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => new SpeechAssetWork(store, registry).RunAsync(
                new(session.Id, session.Voice, [new(row.Id, Guid.NewGuid().ToString("N"), row.Text)]), "test", null, default));
            Assert.AreEqual(1, calls); Assert.AreEqual(BackgroundOperationPhase.Failed, controller.State!.Phase);
            Assert.IsFalse(controller.BlocksNewWork); Assert.IsFalse(store.Load(session.Id).Rows.Single().Confirmed);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    private sealed class FakeNarrator(string root, Action count) : IVideoNarrator
    {
        public IReadOnlyList<VideoVoice> Voices => [];
        public Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
        {
            count(); var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            using (var writer = new WaveFileWriter(path, new(16000, 16, 1))) writer.Write(new byte[3200], 0, 3200);
            return Task.FromResult(new NarratorAudio(path, new("test", "test", "test-revision", "speech")));
        }
        public void Dispose() { }
    }
}
