using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OfficeReaderSpeechTests
{
    [TestMethod]
    public async Task PageRangesAndSelectedOffsetsReadOriginalTextWithoutInventingImageText()
    {
        var root = Path.Combine(Path.GetTempPath(), "Reader-speech-text-" + Guid.NewGuid().ToString("N"));
        var path = OfficeReaderTextTests.MakePdf(root, "source.pdf");
        await using var document = new ReaderDocument(path, Path.Combine(root, "work"), new TestPages(), path);
        try
        {
            Assert.AreEqual("Second page Beta", await new ReaderSpeechRange(1, 1).ReadAsync(document, default));
            Assert.AreEqual("page", await new ReaderSpeechRange(0, 1, 6, 10).ReadAsync(document, default));
            StringAssert.Contains(await new ReaderSpeechRange(0, 2, 6, 6).ReadAsync(document, default), "Alpha");
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ReaderSpeechRange(1, 2).ReadAsync(document, default));
            await using var image = new ReaderDocument(path, Path.Combine(root, "image"), new TestPages());
            Assert.AreEqual("", await new ReaderSpeechRange(0, 2).ReadAsync(image, default));
            var literal = "* literal ** text __ code `x` [brackets](url) 🙂 " + new string('Ж', 1300);
            var parts = ChatSpeechText.Parts(literal, stripMarkup: false);
            Assert.AreEqual(string.Concat(literal.Where(c => !char.IsWhiteSpace(c))), string.Concat(string.Join(" ", parts).Where(c => !char.IsWhiteSpace(c))));
        }
        finally { await document.DisposeAsync(); Directory.Delete(root, true); }
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PauseResumeMergesPcmAndSavedCopyOutlivesTheDocument(bool audit)
    {
        using var fixture = new Fixture(audit);
        var task = fixture.Work.GenerateAsync(fixture.Request(), null, default);
        await fixture.SecondPart.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.Controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(BackgroundOperationPhase.Paused, fixture.Controller.State!.Phase);
        Assert.IsTrue(fixture.Disposed > 0, "The narrator must be disposed before reporting paused.");
        Assert.AreEqual(1, fixture.Controller.State.Checkpoint!.Value.GetProperty("CompletedParts").GetInt32());
        fixture.Block = false; await fixture.Controller.ResumeAsync(default);
        var audio = await task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(3, fixture.Calls, "Resume may only repeat the incomplete part.");
        using (var wave = new WaveFileReader(audio.Path)) Assert.AreEqual(.2, wave.TotalTime.TotalSeconds, .0001);
        var raw = await File.ReadAllBytesAsync(audio.Path);
        StringAssert.Contains(Encoding.UTF8.GetString(raw), "office/reader/speech");
        StringAssert.Contains(Encoding.UTF8.GetString(raw), "actual-revision");
        Assert.IsFalse(Encoding.UTF8.GetString(raw).Contains(fixture.Root), "Export must not contain local paths.");
        var saved = Path.Combine(fixture.Root, "saved.wav");
        await ReaderSpeechExport.SaveAsync(audio.Path, saved, Path.Combine(fixture.Root, "document.pdf"));
        CollectionAssert.AreEqual(raw, File.ReadAllBytes(saved));
        fixture.Session.Dispose(); Assert.IsFalse(Directory.Exists(fixture.Session.DirectoryPath)); Assert.IsTrue(File.Exists(saved));
        Assert.IsFalse(fixture.Controller.BlocksNewWork);
        var reloaded = new BackgroundOperationController(new(Path.Combine(fixture.Root, "background", "operation.json"))); reloaded.Load();
        Assert.IsNull(reloaded.State, "Finished private text must not remain in durable jobs.");
    }

    [TestMethod]
    public async Task CancelAndFailureDoNotLeaveALockedOperationOrRetryAFailedNarrator()
    {
        using var fixture = new Fixture(false);
        var events = new List<OfficeAiEntry>();
        var journal = new OfficeAiJournal("sheet-two", "Speech", (entry, _) => { events.Add(entry); return Task.CompletedTask; });
        var task = fixture.Work.GenerateAsync(fixture.Request(), null, default, journal: journal);
        await fixture.SecondPart.Task.WaitAsync(TimeSpan.FromSeconds(15)); fixture.Session.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.IsFalse(fixture.Controller.BlocksNewWork);
        CollectionAssert.AreEqual(new[] { "running", "cancelled" }, events.Select(e => e.Status).ToArray());
        Assert.AreEqual(events.First().Id, events.Last().Id);
        events.Clear();
        fixture.Session.Dispose(); Assert.IsFalse(Directory.Exists(fixture.Session.DirectoryPath));
        using var failed = new Fixture(false) { Block = false, FailSecond = true };
        var error = await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => failed.Work.GenerateAsync(failed.Request(), null, default, journal: journal));
        Assert.AreEqual("failed", events.Last().Status);
        Assert.AreEqual("sheet-two", events.Last().SheetId);
        Assert.IsNull(events.Last().ResultName);
        Assert.AreEqual("actual-revision", events.Last().Models.Single().Revision);
        Assert.IsInstanceOfType<IOException>(error.InnerException);
        Assert.AreEqual(2, failed.Calls); Assert.IsFalse(failed.Controller.BlocksNewWork);
        Assert.AreEqual(BackgroundOperationPhase.Failed, failed.Controller.State!.Phase);
    }

    [TestMethod]
    public async Task CleanupLeavesLiveDocumentsAndSavedFilesAndNeverOverwritesTheSource()
    {
        using var fixture = new Fixture(false);
        ReaderSpeechSession.RemoveOrphans(Path.Combine(fixture.Root, "temporary"));
        Assert.IsTrue(Directory.Exists(fixture.Session.DirectoryPath));
        var orphan = Path.Combine(fixture.Root, "temporary", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "test"), "temporary");
        ReaderSpeechSession.RemoveOrphans(Path.Combine(fixture.Root, "temporary")); Assert.IsFalse(Directory.Exists(orphan));
        var source = Path.Combine(fixture.Root, "source.pdf"); File.WriteAllText(source, "original");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderSpeechExport.SaveAsync(source, source, source));
        Assert.AreEqual("original", File.ReadAllText(source));
        var store = new BackgroundOperationStore(Path.Combine(fixture.Root, "recovery", "operation.json"));
        var request = fixture.Request();
        store.Save(new() { Id = request.Id, Kind = ReaderSpeechWork.Kind, Title = "Speech", Private = true,
            Input = JsonSerializer.SerializeToElement(request), Phase = BackgroundOperationPhase.Waiting });
        var restored = new BackgroundOperationController(store); restored.Load(); ReaderSpeechWork.DiscardOrphans(restored, Path.Combine(fixture.Root, "temporary"));
        Assert.IsFalse(restored.BlocksNewWork); Assert.AreEqual(BackgroundOperationPhase.Canceled, restored.State!.Phase);
    }

    private sealed class TestPages : IReaderPages
    {
        public IReadOnlyList<ReaderPage> Pages { get; } = [new(600, 800), new(600, 800)];
        public Task<string> RenderAsync(int page, int width, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "Reader-speech-" + Guid.NewGuid().ToString("N"));
        internal readonly ReaderSpeechSession Session;
        internal readonly ReaderSpeechWork Work;
        internal readonly BackgroundOperationController Controller;
        internal readonly TaskCompletionSource SecondPart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block = true, FailSecond;
        internal int Calls, Disposed;
        private readonly IDisposable _audit;
        private readonly BackgroundOperationController? _previous = ApplicationBackgroundOperations.Current;
        internal Fixture(bool audit)
        {
            Directory.CreateDirectory(Root); Session = new(Path.Combine(Root, "temporary"));
            Controller = new(new(Path.Combine(Root, "background", "operation.json"))); ApplicationBackgroundOperations.Current = Controller;
            _audit = ComponentAudit.UseTestStore(Path.Combine(Root, "audit"), audit);
            var registry = new VideoNarratorRegistry(); registry.Register("silero56", () => new Fake(this)); Work = new(Session, registry);
        }
        internal ReaderSpeechRequest Request() => new(Guid.NewGuid().ToString("N"), new string('А', 900), new("silero56", "ru", 100, new("xenia", Device: "cpu")), "Speech");
        public void Dispose() { Session.Dispose(); ApplicationBackgroundOperations.Current = _previous; _audit.Dispose(); Directory.Delete(Root, true); }
        private sealed class Fake(Fixture owner) : IVideoNarrator
        {
            public IReadOnlyList<VideoVoice> Voices => [];
            public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
            {
                var call = ++owner.Calls;
                if (call == 2 && owner.FailSecond) throw new IOException("native failure");
                if (call == 2 && owner.Block) { owner.SecondPart.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                var path = Path.Combine(owner.Root, "raw-" + call + ".wav");
                using (var wave = new WaveFileWriter(path, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
                return new(path, new("actual-voice", "Actual voice", "actual-revision", "speech"));
            }
            public void Dispose() => owner.Disposed++;
        }
    }
}
