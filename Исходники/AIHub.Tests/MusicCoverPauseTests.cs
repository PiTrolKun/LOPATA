using System.IO;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicCoverPauseTests
{
    [TestMethod]
    public async Task PausedCoverReleasesModelGateAndResumesItsOwnOperationAfterAnotherUtility()
    {
        var root = MusicOutputTests.Temporary(); var original = ApplicationBackgroundOperations.Current;
        var background = new BackgroundOperationController(new BackgroundOperationStore(Path.Combine(root, "background.json")));
        ApplicationBackgroundOperations.Current = background;
        try
        {
            var store = new MusicCoverStore(root); var model = new MusicCoverGenerationTests.SummaryModel(); var worker = new PausingWorker();
            var work = new MusicCoverWork(store, () => model, new(worker), (_, _) => Task.FromResult<IReadOnlyList<ManagedModelArtifactCard>>([]));
            var input = new MusicCoverInput(Guid.NewGuid().ToString("N"), "Artist", "Title", "Song lyrics", "jazz", "unused", "unused.gguf"); store.Create(input);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = work.RunAsync(input, _ => { }, "Covers", cancel.Token);
            await worker.Entered.Task.WaitAsync(cancel.Token);
            await background.PauseAsync().WaitAsync(cancel.Token);
            var cover = background.FindOperation(MusicCoverWork.Kind)!;
            Assert.AreEqual(BackgroundOperationPhase.Paused, cover.Phase); Assert.IsFalse(background.BlocksNewWork);
            await ApplicationBackgroundOperations.RunAsync("test.utility", "Other utility", null, new { }, _ => Task.FromResult(1), cancel.Token);
            await background.ResumeAsync(cover.Id, cancel.Token);
            var ready = await run.WaitAsync(cancel.Token);
            Assert.HasCount(4, ready.Ready); Assert.AreEqual(1, model.Calls);
            Assert.AreEqual(1, worker.Written.Calls.Count(i => i == 0), "Finished images survive pause.");
        }
        finally { ApplicationBackgroundOperations.Current = original; Directory.Delete(root, true); }
    }
    private sealed class PausingWorker : IImageGenerationWorker
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MusicCoverGenerationTests.ImageWorker Written { get; } = new();
        private bool _paused;
        public async Task GenerateAsync(ImageGenerationRequest request, int index, IReadOnlyList<ManagedModelArtifactCard> cards, string promptFile, string output, CancellationToken token)
        {
            if (index == 1 && !_paused) { _paused = true; Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            await Written.GenerateAsync(request, index, cards, promptFile, output, token);
        }
    }
}
