using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BackgroundSwitchingTests
{
    private string _root = null!;
    private BackgroundOperationStore Store => new(Path.Combine(_root, "operation.json"));
    [TestInitialize] public void Init() => _root = Path.Combine(Path.GetTempPath(), "lopata-switch-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static BackgroundOperationState Input(string kind) => new() { Kind = kind, Title = kind, Input = JsonSerializer.SerializeToElement(new { Book = "source" }) };

    [TestMethod]
    public async Task FailedPauseRetirementStillBlocksOtherModels()
    {
        var controller = new BackgroundOperationController(Store);
        var running = controller.RunAsync(Input("books"), async token => { await Task.Delay(Timeout.Infinite, token); return 1; },
            () => throw new IOException("model retirement was not confirmed"), default);
        await Assert.ThrowsAsync<IOException>(() => controller.PauseAsync());
        await Assert.ThrowsAsync<IOException>(async () => await running);
        Assert.IsTrue(controller.State!.RequiresDecision); Assert.IsTrue(controller.BlocksNewWork);
        Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary());
        Assert.HasCount(0, controller.PausedOperations);
    }

    [TestMethod]
    public async Task ApplicationCancelTargetsItsOwnParkedJobWhileOtherJobRuns()
    {
        var previous = ApplicationBackgroundOperations.Current; var previousExit = ApplicationBackgroundOperations.ExitToken;
        var controller = new BackgroundOperationController(Store);
        using var firstLifetime = new CancellationTokenSource(); using var otherLifetime = new CancellationTokenSource();
        ApplicationBackgroundOperations.Current = controller; ApplicationBackgroundOperations.ExitToken = default;
        Task<int>? first = null, other = null;
        try
        {
            first = ApplicationBackgroundOperations.RunAsync("books", "Books", null, new { }, async token => { await Task.Delay(Timeout.Infinite, token); return 1; }, firstLifetime.Token);
            var firstId = controller.State!.Id; await controller.PauseAsync();
            other = ApplicationBackgroundOperations.RunAsync("music", "Music", null, new { }, async token => { await Task.Delay(Timeout.Infinite, token); return 2; }, otherLifetime.Token);
            var otherId = controller.State!.Id;
            firstLifetime.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Assert.AreEqual(BackgroundOperationPhase.Canceled, Store.Operation(firstId).Load()!.Phase);
            Assert.AreEqual(otherId, controller.State.Id); Assert.IsTrue(controller.IsRunning);
            Assert.IsFalse(controller.PausedOperations.Any(job => job.Id == firstId));
        }
        finally
        {
            firstLifetime.Cancel(); otherLifetime.Cancel();
            if (first is not null) try { await first; } catch (OperationCanceledException) { }
            if (other is not null) try { await other; } catch (OperationCanceledException) { }
            ApplicationBackgroundOperations.Current = previous; ApplicationBackgroundOperations.ExitToken = previousExit;
        }
    }

    [TestMethod]
    public async Task PausedJobAllowsAuxiliaryAndAnotherScenarioThenResumesItsOwnCheckpoint()
    {
        var controller = new BackgroundOperationController(Store); using var lifetime = new CancellationTokenSource();
        var first = Input("books"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var attempts = 0; var releases = 0;
        var original = controller.RunAsync(first, async token =>
        {
            if (++attempts == 1) { controller.SaveCheckpoint("book portion"); entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            Assert.AreEqual("book portion", controller.State!.Checkpoint!.Value.GetString()); return 7;
        }, () => { releases++; return Task.CompletedTask; }, lifetime.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await controller.PauseAsync();
            Assert.IsFalse(controller.IsRunning); Assert.IsFalse(controller.BlocksNewWork); Assert.AreEqual(1, releases);
            Assert.IsTrue(controller.HasLiveOperation(first.Id), "A live paused continuation must remain resumable without launching another restoration handler.");
            using (controller.BeginAuxiliary())
            {
                Assert.IsTrue(controller.IsRunning);
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunAsync(Input("blocked"), _ => Task.FromResult(0), () => Task.CompletedTask, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResumeAsync(first.Id, default));
            }
            var second = Input("music");
            Assert.AreEqual(42, await controller.RunAsync(second, _ => { controller.SaveCheckpoint("music portion"); return Task.FromResult(42); }, () => Task.CompletedTask, default));
            Assert.AreEqual(second.Id, controller.State!.Id); Assert.AreEqual("music portion", controller.State.Checkpoint!.Value.GetString());
            Assert.AreEqual(first.Id, controller.PausedOperations.Single().Id); Assert.IsFalse(original.IsCompleted);
            await controller.ResumeAsync(first.Id, default); Assert.AreEqual(7, await original.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual(2, attempts); Assert.HasCount(0, controller.PausedOperations);
        }
        finally { lifetime.Cancel(); try { await original; } catch (OperationCanceledException) { } }
    }

    [TestMethod]
    public async Task MultiplePausedJobsSurviveRestartWithoutAutomaticResume()
    {
        var controller = new BackgroundOperationController(Store); using var exit = new CancellationTokenSource();
        async Task<int> Execute(CancellationToken token) { controller.SaveCheckpoint(controller.State!.Kind); await Task.Delay(Timeout.Infinite, token); return 0; }
        var a = Input("a"); var b = Input("b");
        var first = controller.RunAsync(a, Execute, () => Task.CompletedTask, exit.Token); await controller.PauseAsync();
        var second = controller.RunAsync(b, Execute, () => Task.CompletedTask, exit.Token); await controller.PauseAsync();
        Assert.HasCount(2, controller.PausedOperations); controller.CheckpointForExit(); exit.Cancel();
        try { await first; } catch (OperationCanceledException) { }
        try { await second; } catch (OperationCanceledException) { }
        await controller.WaitForAllIdleAsync();
        var restored = new BackgroundOperationController(Store); restored.Load();
        Assert.HasCount(2, restored.PausedOperations); Assert.IsFalse(restored.IsRunning); Assert.IsFalse(restored.BlocksNewWork);
        var resumed = new List<string>();
        foreach (var kind in new[] { "a", "b" }) restored.Register(kind, (state, token) => restored.RunAsync(state,
            _ => { resumed.Add(state.Id); Assert.AreEqual(state.Kind, state.Checkpoint!.Value.GetString()); return Task.FromResult(1); }, () => Task.CompletedTask, token));
        await restored.ResumeAsync(a.Id, default); Assert.AreEqual(a.Id, restored.State!.Id);
        Assert.AreEqual(b.Id, restored.PausedOperations.Single().Id);
        await restored.ResumeAsync(b.Id, default); Assert.HasCount(0, restored.PausedOperations);
        CollectionAssert.AreEqual(new[] { a.Id, b.Id }, resumed);
    }

    [TestMethod]
    public async Task CancelingParkedJobCannotRetireAnotherScenariosModel()
    {
        var controller = new BackgroundOperationController(Store); using var firstLifetime = new CancellationTokenSource(); using var secondLifetime = new CancellationTokenSource();
        var releases = 0;
        var first = controller.RunAsync(Input("books"), async token => { await Task.Delay(Timeout.Infinite, token); return 1; },
            () => { releases++; return Task.CompletedTask; }, firstLifetime.Token);
        await controller.PauseAsync(); Assert.AreEqual(1, releases);
        var other = Input("other"); var second = controller.RunAsync(other, async token => { await Task.Delay(Timeout.Infinite, token); return 2; }, () => Task.CompletedTask, secondLifetime.Token);
        try
        {
            firstLifetime.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Assert.AreEqual(1, releases); Assert.AreEqual(other.Id, controller.State!.Id); Assert.IsTrue(controller.IsRunning);
        }
        finally { secondLifetime.Cancel(); try { await second; } catch (OperationCanceledException) { } }
    }
}
