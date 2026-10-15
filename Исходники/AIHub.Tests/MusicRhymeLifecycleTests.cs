using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeLifecycleTests
{
    [TestMethod, TestCategory("RhymeRuntime")]
    public async Task PauseReleasesModelAndRestartResumesCheckpointWithoutArchivingDraft()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RHYME_RUNTIME") != "1") { Assert.Inconclusive("Opt-in real corpus test"); return; }
        var root = Path.Combine(Path.GetTempPath(), "lopata-rhyme-life-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var previous = ApplicationBackgroundOperations.Current;
        var previousExit = ApplicationBackgroundOperations.ExitToken;
        using var lifetime = new CancellationTokenSource();
        var shared = new Shared();
        try
        {
            ApplicationBackgroundOperations.ExitToken = CancellationToken.None;
            var store = new BackgroundOperationStore(Path.Combine(root, "operation.json"));
            var controller = new BackgroundOperationController(store); ApplicationBackgroundOperations.Current = controller;
            var request = new MusicRhymeRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "Кот\nСад", 0, 7,
                "core", Path.Combine(root, "Qwen3-8B-Q4_K_M.gguf"), root, "ru");
            var step = new MusicRhymeStep { Request = request, Original = request.Text, Program = request.Text, ProgramReady = true,
                Reference = "{}", Stage = "Model", Analysis = MusicRhymeAnalyzer.Analyze(request.Source, _ => []) };
            var work = new MusicRhymeWork(() => new BlockingModel(shared));
            var running = work.RunAsync(step, s => s, new InlineProgress<RagBookProgress>(_ => { }), lifetime.Token);
            await shared.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, shared.Disposed); Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
            Assert.IsFalse(controller.BlocksNewWork);
            using (controller.BeginAuxiliary()) Assert.IsTrue(controller.IsRunning);
            var checkpoint = controller.State.Checkpoint!.Value.Deserialize<MusicRhymeStep>()!;
            Assert.IsTrue(checkpoint.ProgramReady); Assert.IsFalse(checkpoint.ModelReady);
            controller.CheckpointForExit(); lifetime.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await running);
            await controller.WaitForAllIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var restored = new BackgroundOperationController(store); ApplicationBackgroundOperations.Current = restored;
            MusicRhymeStep? finished = null;
            restored.Register(MusicRhymeWork.Kind, async (state, token) => finished = await work.RunAsync(
                state.Checkpoint!.Value.Deserialize<MusicRhymeStep>()!, s => s, new InlineProgress<RagBookProgress>(_ => { }), token, state));
            restored.Load(); Assert.IsTrue(restored.State!.UserPaused);
            await restored.ResumeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsNotNull(finished); Assert.AreEqual("Ready", finished.Stage); Assert.AreEqual("Кот\nСвод", finished.Model);
            Assert.AreEqual(2, shared.Calls); Assert.AreEqual(2, shared.Disposed);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Results")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "operation.json")));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(root, "Jobs"), "*.json").Length);
        }
        finally
        {
            lifetime.Cancel(); ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousExit;
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("lopata-rhyme-life-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(full, true);
        }
    }
    private sealed class Shared
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls, Disposed;
    }
    private sealed class BlockingModel(Shared state) : IMusicRhymeModel
    {
        public async Task<string> RewriteAsync(DebugModelInfo model, string prompt, CancellationToken token)
        {
            if (Interlocked.Increment(ref state.Calls) == 1) { state.Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return "Кот\nСвод";
        }
        public void Dispose() => Interlocked.Increment(ref state.Disposed);
    }
}
