using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class BackgroundOperationTests
{
    private string _folder = null!;
    [TestInitialize] public void Initialize() => _folder = Path.Combine(Path.GetTempPath(), "lopata-background-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup()
    {
        var path = Path.GetFullPath(_folder);
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("lopata-background-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private BackgroundOperationStore Store => new(Path.Combine(_folder, "operation.json"));
    private static BackgroundOperationState Input(double elapsed = 0) => new()
    { Kind = "test.queue", Title = "Queue", Input = JsonSerializer.SerializeToElement(new[] { "first", "second" }), ElapsedSeconds = elapsed };

    [TestMethod]
    public async Task PauseWaitsForRetirementAndResumesOnlyUnfinishedStage()
    {
        var controller = new BackgroundOperationController(Store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int first = 0, second = 0;
        var work = controller.RunAsync(Input(), async ct =>
        {
            if (controller.State?.Checkpoint is null) { first++; controller.SaveCheckpoint(1); }
            second++; started.TrySetResult();
            if (second == 1) await Task.Delay(Timeout.Infinite, ct);
            return "done";
        }, async () => { retiring.TrySetResult(); await retired.Task; }, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = controller.PauseAsync();
        await retiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(BackgroundOperationPhase.Pausing, controller.State!.Phase);
        Assert.IsFalse(pause.IsCompleted);
        retired.SetResult(); await pause.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase);
        Assert.IsTrue(Store.Load()!.UserPaused);
        await controller.ResumeAsync(CancellationToken.None);
        Assert.AreEqual("done", await work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, first); Assert.AreEqual(2, second);
    }

    [TestMethod]
    public void RestartRestoresWaitingWithoutOverridingExplicitPause()
    {
        Store.Save(Input() with { Phase = BackgroundOperationPhase.Running, UserPaused = true, Checkpoint = JsonSerializer.SerializeToElement(4) });
        var controller = new BackgroundOperationController(Store); controller.Load(); controller.SetCountdown(true);
        Assert.AreEqual(BackgroundOperationPhase.Waiting, controller.State!.Phase);
        Assert.IsTrue(controller.State.UserPaused); Assert.AreEqual(4, controller.State.Checkpoint!.Value.GetInt32());
    }

    [TestMethod]
    public async Task CompletionCannotTurnIntoPhantomPausedTask()
    {
        var controller = new BackgroundOperationController(Store);
        await controller.RunAsync(Input(), _ => Task.FromResult(1), () => Task.CompletedTask, CancellationToken.None);
        await controller.PauseAsync();
        Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State!.Phase);
        Assert.IsFalse(controller.HasPending);
    }

    [TestMethod]
    public async Task ResultAttentionSurvivesNewOperationAndNeedsMatchingAcknowledgement()
    {
        var controller = new BackgroundOperationController(Store); var first = Input(31);
        await controller.RunAsync(first, _ => Task.FromResult(1), () => Task.CompletedTask, CancellationToken.None);
        await controller.RunAsync(Input(), _ => Task.FromResult(2), () => Task.CompletedTask, CancellationToken.None);
        controller.Acknowledge("other"); Assert.IsTrue(controller.State!.NeedsAttention);
        controller.Acknowledge(first.Id); Assert.IsFalse(Store.Load()!.NeedsAttention);
    }

    [TestMethod]
    public void InvalidCheckpointWritePreservesPreviousFile()
    {
        var original = Input(); Store.Save(original);
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => Store.Save(original with { ElapsedSeconds = double.NaN }));
        Assert.AreEqual(original.Id, Store.Load()!.Id);
    }

    [TestMethod]
    public async Task NotificationObserverCannotInvalidateCompletedWork()
    {
        var controller = new BackgroundOperationController(Store);
        controller.Changed += () => throw new InvalidOperationException("observer");
        controller.Completed += _ => throw new InvalidOperationException("observer");
        Assert.AreEqual(42, await controller.RunAsync(Input(31), _ => Task.FromResult(42), () => Task.CompletedTask, CancellationToken.None));
        Assert.AreEqual(BackgroundOperationPhase.Completed, Store.Load()!.Phase);
    }

    [TestMethod]
    public async Task ExitKeepsCheckpointAndExplicitPauseAcrossRestart()
    {
        var controller = new BackgroundOperationController(Store);
        using var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = controller.RunAsync(Input(31), async ct =>
        {
            controller.SaveCheckpoint("confirmed"); started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); return 1;
        }, () => Task.CompletedTask, lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        controller.CheckpointForExit(); lifetime.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await work);
        await controller.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var restored = new BackgroundOperationController(Store); restored.Load();
        Assert.IsTrue(restored.State!.UserPaused); Assert.IsTrue(restored.State.ElapsedSeconds >= 31);
        Assert.AreEqual("confirmed", restored.State.Checkpoint!.Value.GetString());
        restored.CheckpointForExit(); Assert.IsTrue(Store.Load()!.ElapsedSeconds >= 31);
    }

    [TestMethod]
    public async Task RefusedRequestCanBeResolvedWithoutDiscardingAnotherPendingTask()
    {
        var controller = new BackgroundOperationController(Store); var input = Input();
        await Assert.ThrowsAsync<BackgroundOperationWaitingException>(async () => await controller.RunAsync<int>(input,
            _ => throw new BackgroundOperationWaitingException("context_full"), () => Task.CompletedTask, CancellationToken.None));
        Assert.IsTrue(controller.HasPending); Assert.IsTrue(controller.State!.RequiresDecision);
        controller.DiscardPending("foreign"); Assert.IsTrue(controller.HasPending);
        controller.DiscardPending(input.Id); Assert.IsFalse(controller.HasPending);
        await controller.RunAsync(Input(), _ => Task.FromResult(42), () => Task.CompletedTask, CancellationToken.None);
        Assert.AreEqual(BackgroundOperationPhase.Completed, Store.Load()!.Phase);
    }

    [TestMethod]
    public async Task PauseCancellationCallbackMayCheckpointOnAnotherThread()
    {
        var controller = new BackgroundOperationController(Store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var work = controller.RunAsync(Input(), async ct =>
        {
            if (++attempts > 1) return 42;
            // Captures the operation scope, but needs the controller lock from another thread.
            using var callback = ct.Register(() => Task.Run(() => controller.SaveCheckpoint("canceling")).GetAwaiter().GetResult());
            started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return 0;
        }, () => Task.CompletedTask, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("canceling", Store.Load()!.Checkpoint!.Value.GetString());
        await controller.ResumeAsync(CancellationToken.None);
        Assert.AreEqual(42, await work.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ViewingLatestResultDoesNotAcknowledgeAnOlderUnviewedResult()
    {
        var controller = new BackgroundOperationController(Store); var first = Input(31); var second = Input(31);
        await controller.RunAsync(first, _ => Task.FromResult(1), () => Task.CompletedTask, CancellationToken.None);
        await controller.RunAsync(second, _ => Task.FromResult(2), () => Task.CompletedTask, CancellationToken.None);
        controller.Acknowledge(second.Id); Assert.IsTrue(controller.State!.NeedsAttention);
        Assert.AreEqual(first.Id, controller.State.Notice!.Id);
        var restored = new BackgroundOperationController(Store); restored.Load();
        restored.Acknowledge(first.Id); Assert.IsFalse(Store.Load()!.NeedsAttention);
        Assert.HasCount(0, restored.State!.Notices);
    }
}
