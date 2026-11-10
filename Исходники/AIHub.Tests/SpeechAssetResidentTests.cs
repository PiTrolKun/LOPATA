using System.IO;
using AIHub.Services;
using AIHub.Services.SpeechAssets;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class SpeechAssetResidentTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "speech-resident-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task WarmupAndTwoRunsShareOneNarratorUntilRetirement(bool audit)
    {
        using var policy = ComponentAudit.UseTestStore(Path.Combine(_root, "audit"), audit);
        var previous = ApplicationBackgroundOperations.Current; ApplicationBackgroundOperations.Current = null;
        var created = 0; var disposed = 0; var calls = 0; var privateWarmup = false;
        var registry = new VideoNarratorRegistry();
        registry.Register("silero56", () => { created++; return new Probe(_root, token =>
        { calls++; if (calls == 1) privateWarmup = !VideoNarrationWorkspace.PublishOutput; return Task.CompletedTask; }, () => disposed++); });
        var resident = new SpeechAssetModelSession(registry, Path.Combine(_root, "warm"));
        var choice = new VideoNarratorChoice("silero56", "ru", 100, new("xenia", Device: "cpu"));
        var store = new SpeechAssetStore(Path.Combine(_root, "sessions"));
        var session = new SpeechAssetSession { Voice = choice, Rows = SpeechAssetMarkdown.Parse("- Первая реплика\n- Вторая реплика") }; store.Save(session);
        try
        {
            await resident.WarmAsync(choice, default); await resident.WarmAsync(choice, default);
            Assert.AreEqual(1, calls); Assert.IsTrue(privateWarmup); Assert.IsTrue(VideoNarrationWorkspace.PublishOutput);
            foreach (var row in session.Rows)
                await new SpeechAssetWork(store, resident: resident).RunAsync(new(session.Id, choice,
                    [new(row.Id, Guid.NewGuid().ToString("N"), row.Text)]), "probe", null, default);
            Assert.AreEqual(1, created); Assert.AreEqual(0, disposed); Assert.AreEqual(3, calls);
            Assert.IsTrue(resident.IsWarm(choice)); Assert.IsTrue(store.Load(session.Id).Rows.All(r => r.Takes.Count == 1));
            await resident.RetireAsync(); Assert.AreEqual(1, disposed); Assert.IsFalse(resident.IsWarm(choice));
        }
        finally { await resident.RetireAsync(); ApplicationBackgroundOperations.Current = previous; }
    }

    [TestMethod]
    public async Task RetirementCancelsInFlightWarmupAndAllowsLaterExplicitWarmup()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0; var disposed = 0;
        var registry = new VideoNarratorRegistry();
        registry.Register("silero56", () => new Probe(_root, async token =>
        { if (++calls == 1) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } }, () => disposed++));
        var resident = new SpeechAssetModelSession(registry, Path.Combine(_root, "warm"));
        var choice = new VideoNarratorChoice("silero56", "ru", 100, new("xenia"));
        try
        {
            var warm = resident.WarmAsync(choice, default); await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await resident.RetireAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<OperationCanceledException>(() => warm);
            Assert.IsFalse(resident.IsWarm(choice)); Assert.AreEqual(1, disposed);
            await resident.WarmAsync(choice, default); Assert.IsTrue(resident.IsWarm(choice));
        }
        finally { await resident.RetireAsync(); }
    }

    internal sealed class Probe(string root, Func<CancellationToken, Task> before, Action disposed) : IVideoNarrator
    {
        public IReadOnlyList<VideoVoice> Voices => [];
        public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token)
        {
            await before(token); Directory.CreateDirectory(root);
            var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            using (var wave = new WaveFileWriter(file, new(16000, 16, 1))) wave.Write(new byte[3200], 0, 3200);
            return new(file, new("test", "test", "revision", "speech"));
        }
        public void Dispose() => disposed();
    }
}
