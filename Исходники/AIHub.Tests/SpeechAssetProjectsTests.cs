using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SpeechAssetProjectsTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "speech-projects-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void BulkConfirmationSkipsMissingStaleUnselectedAndCorruptAudio()
    {
        var (store, session) = Fixture(6);
        AddTake(store, session, session.Rows[0]);
        AddTake(store, session, session.Rows[1]); session.Rows[1].Edit("changed");
        AddTake(store, session, session.Rows[2]); session.Rows[2].Takes[0].Selected = false;
        AddTake(store, session, session.Rows[3]); File.Delete(store.FileFor(session.Id, session.Rows[3].Takes[0].File));
        AddTake(store, session, session.Rows[4]); File.AppendAllText(store.FileFor(session.Id, session.Rows[4].Takes[0].File), "broken");
        store.Save(session);
        Assert.AreEqual(1, store.ConfirmAll(session.Id));
        var loaded = store.Load(session.Id);
        Assert.IsTrue(loaded.Rows[0].Confirmed); Assert.IsTrue(loaded.Rows.Skip(1).All(r => !r.Confirmed));
        Assert.AreEqual(0, store.ConfirmAll(session.Id));
    }

    [TestMethod]
    public void ManagedProjectsSeparateAutomaticAndFixedAndKeepQueueAndAllTakes()
    {
        var (store, session) = Fixture(2); AddTake(store, session, session.Rows[0]);
        session.Rows[0].Confirm(); session.Rows[1].Edit("pronunciation edit");
        session.ExportFormat = MusicAudioFormat.Mp3; session.ExportBitrate = 96;
        Assert.HasCount(0, store.ListProjects(), "Recovery drafts are not catalogue projects.");
        var job = new SpeechAssetJob(session.Id, session.Voice, [new(session.Rows[1].Id, Guid.NewGuid().ToString("N"), session.Rows[1].Text)],
            store.Root, RunId: Guid.NewGuid().ToString("N"));
        store.BeginRun(session, job); store.BeginRun(session, job);
        Assert.IsFalse(store.ListProjects().Single().Manual); Assert.HasCount(1, store.Load(session.Id).Runs);
        store.RunState(job, "Interrupted");
        session = store.Load(session.Id); store.FixProject(session); store.RenameProject(session, "Notification pack");
        var reopened = new SpeechAssetStore(store.Root).ListProjects().Single();
        Assert.IsTrue(reopened.Manual); Assert.AreEqual("Notification pack", reopened.Name);
        Assert.AreEqual(job.Items[0].TakeId, reopened.PendingJob!.Items[0].TakeId);
        Assert.AreEqual(MusicAudioFormat.Mp3, reopened.ExportFormat); Assert.IsTrue(reopened.Rows[0].Confirmed);
        Assert.AreEqual("Interrupted", reopened.Runs.Single().State);
        var take = reopened.Rows[0].Takes.Single();
        Assert.AreEqual(take.Sha256, SpeechAssetStore.Hash(store.FileFor(session.Id, take.File)));
        var draft = store.CreateDraft(reopened);
        Assert.IsTrue(draft.Number > reopened.Number); Assert.IsFalse(draft.Persistent); Assert.HasCount(0, draft.Rows);
        Assert.AreEqual(reopened.Voice, draft.Voice); Assert.HasCount(0, draft.Runs);
    }

    [TestMethod]
    public void BrokenPrimaryRecoversDurableBackupAndIgnoresInterruptedTemporaryWrite()
    {
        var (store, session) = Fixture(1); AddTake(store, session, session.Rows[0]);
        store.Save(session); session.Rows[0].FileName = "new"; store.Save(session);
        var file = store.FileFor(session.Id, "session.json");
        File.WriteAllText(file, "{broken"); File.WriteAllText(file + ".interrupted.tmp", "{incomplete");
        var recovered = store.Load(session.Id);
        Assert.AreEqual("0001", recovered.Rows[0].FileName); Assert.HasCount(1, recovered.Rows[0].Takes);
        using var valid = JsonDocument.Parse(File.ReadAllText(file));
        recovered.Rows[0].Confirm(); store.Save(recovered);
        Assert.IsTrue(store.Load(session.Id).Rows[0].Confirmed);
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.damaged-*"));
    }

    [TestMethod]
    public async Task StopAndRestartReusesDurablePartsWithoutLosingOtherConfirmations()
    {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        var (store, session) = Fixture(2);
        session.Voice = new("silero56", "ru", 100, new("xenia"));
        AddTake(store, session, session.Rows[0]);
        session.Rows[1].Text = string.Join(" ", Enumerable.Repeat("This is a long line for a recoverable recording.", 40));
        session.Rows[1].SourceText = session.Rows[1].Text;
        var parts = ChatSpeechText.Parts(session.Rows[1].Text, stripMarkup: false); Assert.IsTrue(parts.Count > 1);
        var item = new SpeechAssetJobItem(session.Rows[1].Id, Guid.NewGuid().ToString("N"), session.Rows[1].Text);
        session.PendingJob = new(session.Id, session.Voice, [item], store.Root); store.Save(session);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var registry = new VideoNarratorRegistry();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        registry.Register("silero56", () => new ProbeNarrator(_root, async token =>
        { if (++calls == 2) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } }));
        try
        {
            var run = new SpeechAssetWork(store, registry).RunAsync(session.PendingJob, "probe", null, cancel.Token);
            await reached.Task.WaitAsync(cancel.Token);
            Assert.AreEqual(1, store.ConfirmAll(session.Id)); cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
            var restarted = new SpeechAssetStore(store.Root); var loaded = restarted.Load(session.Id);
            Assert.IsNotNull(loaded.PendingJob); Assert.IsTrue(loaded.Rows[0].Confirmed);
            await new SpeechAssetWork(restarted, registry).RunAsync(loaded.PendingJob, "resume", null, default);
            loaded = restarted.Load(session.Id);
            Assert.AreEqual(parts.Count + 1, calls, "The first completed fragment must be reused.");
            Assert.IsTrue(loaded.Rows[0].Confirmed); Assert.HasCount(1, loaded.Rows[1].Takes); Assert.IsNull(loaded.PendingJob);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }

    private (SpeechAssetStore, SpeechAssetSession) Fixture(int count)
    {
        Directory.CreateDirectory(_root);
        var store = new SpeechAssetStore(Path.Combine(_root, "sessions"));
        var session = new SpeechAssetSession { Rows = SpeechAssetMarkdown.Parse(string.Join("\n", Enumerable.Range(1, count).Select(i => "- Line " + i))) };
        store.Save(session); return (store, session);
    }
    [TestMethod]
    public async Task StreamingRetriesThreeTimesSkipsFailureAndCompletesFollowingLines()
    {
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        var (store, session) = Fixture(2); session.Voice = new("silero56", "ru", 100, new("xenia"));
        var job = new SpeechAssetJob(session.Id, session.Voice,
            session.Rows.Select(r => new SpeechAssetJobItem(r.Id, Guid.NewGuid().ToString("N"), r.Text)).ToArray(), store.Root, true, Guid.NewGuid().ToString("N"));
        store.BeginRun(session, job);
        var failures = 0; var successes = 0; var retryNumbers = new List<int>();
        var registry = new VideoNarratorRegistry();
        registry.Register("silero56", () => new TextProbeNarrator(_root, text =>
        { if (text == "Line 1") { failures++; throw new IOException("test model failure"); } successes++; }));
        try
        {
            await new SpeechAssetWork(store, registry).RunAsync(job, "stream", null, default, retry: (_, number) => retryNumbers.Add(number));
            var result = store.Load(session.Id);
            Assert.AreEqual(4, failures); Assert.AreEqual(1, successes);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, retryNumbers);
            Assert.HasCount(0, result.Rows[0].Takes); Assert.AreEqual(4, result.Rows[0].FailedAttempts);
            Assert.AreEqual("test model failure", result.Rows[0].LastError);
            Assert.HasCount(1, result.Rows[1].Takes); Assert.IsNull(result.PendingJob);
            Assert.AreEqual("CompletedWithErrors", result.Runs.Single().State);
        }
        finally { ApplicationBackgroundOperations.Current = previous; }
    }
    private sealed class TextProbeNarrator(string root, Action<string> before) : IVideoNarrator
    {
        public IReadOnlyList<VideoVoice> Voices => [];
        public Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
        {
            before(text); var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            using (var wave = new WaveFileWriter(path, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
            return Task.FromResult(new NarratorAudio(path, new("test", "test", "revision", "speech")));
        }
        public void Dispose() { }
    }
    private static void AddTake(SpeechAssetStore store, SpeechAssetSession session, SpeechAssetRow row)
    {
        var id = Guid.NewGuid().ToString("N"); var relative = "audio/" + id + "/recording.wav"; var file = store.FileFor(session.Id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var writer = new WaveFileWriter(file, new(16000, 16, 1))) writer.Write(new byte[3200], 0, 3200);
        row.Takes.Add(new() { Id = id, File = relative, Text = row.Text, Selected = true, Sha256 = SpeechAssetStore.Hash(file), Seconds = .1 });
    }
    private sealed class ProbeNarrator(string root, Func<CancellationToken, Task> before) : IVideoNarrator
    {
        public IReadOnlyList<VideoVoice> Voices => [];
        public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
        {
            await before(token); var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            using (var wave = new WaveFileWriter(path, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
            return new(path, new("test", "test", "revision", "speech"));
        }
        public void Dispose() { }
    }
}
