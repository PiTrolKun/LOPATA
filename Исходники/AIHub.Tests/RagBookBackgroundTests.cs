using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagBookBackgroundTests
{
    [TestMethod]
    public async Task AcquisitionCanPauseAndResumeWithNoPageOrWindow()
    {
        using var files = new Files(); using var lifetime = new CancellationTokenSource();
        var controller = files.Controller(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var work = new RagBookAcquisitionWork(controller, _ => new Preparation(async (id, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new() { ModelArtifactId = id, Status = ManagedModelStatuses.Installed };
        }));
        var running = work.RunAsync(files.Request, "Book files", new Progress<ManagedModelDownloadProgress>(), lifetime.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await controller.PauseAsync();
            Assert.AreEqual(BackgroundOperationPhase.Paused, controller.State!.Phase); Assert.IsTrue(controller.State.UserPaused);
            Assert.IsFalse(running.IsCompleted); Assert.IsTrue(controller.State.Checkpoint.HasValue);
            await controller.ResumeAsync(default); await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, attempts); Assert.AreEqual(BackgroundOperationPhase.Completed, controller.State.Phase);
        }
        finally { lifetime.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExitAndReloadPreserveRequestAndExplicitPause(bool userPaused)
    {
        using var files = new Files(); using var lifetime = new CancellationTokenSource();
        var controller = files.Controller(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new RagBookAcquisitionWork(controller, _ => new Preparation(async (_, token) =>
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(); }));
        var running = work.RunAsync(files.Request, "Book files", new Progress<ManagedModelDownloadProgress>(), lifetime.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (userPaused) await controller.PauseAsync();
        controller.CheckpointForExit(); lifetime.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        var reloaded = files.Controller(); reloaded.Load();
        Assert.AreEqual(BackgroundOperationPhase.Waiting, reloaded.State!.Phase);
        Assert.AreEqual(userPaused, reloaded.State.UserPaused);
        CollectionAssert.AreEqual(files.Request.Ids, RagBookAcquisitionWork.Read(reloaded.State).Ids);
        var restoredWork = new RagBookAcquisitionWork(reloaded, _ => new Preparation((id, _) => Task.FromResult(new ManagedModelArtifactCard
        { ModelArtifactId = id, Status = ManagedModelStatuses.Installed })));
        reloaded.Register(RagBookAcquisitionWork.Kind, (state, token) => restoredWork.RunAsync(RagBookAcquisitionWork.Read(state),
            state.Title, new Progress<ManagedModelDownloadProgress>(), token, state));
        Assert.IsTrue(reloaded.CanRestore);
        await reloaded.ResumeAsync(default);
        Assert.AreEqual(BackgroundOperationPhase.Completed, reloaded.State.Phase);
    }

    [TestMethod]
    public async Task OtherPendingOperationIsNotOverwrittenAndNoLicenseIsAsked()
    {
        using var files = new Files(); var controller = files.Controller();
        var original = new BackgroundOperationState { Kind = "another-work", Title = "Other work",
            Input = JsonSerializer.SerializeToElement(new { }), Phase = BackgroundOperationPhase.Waiting };
        new BackgroundOperationStore(files.StatePath).Save(original); controller.Load();
        var previous = ComponentLicenseGate.ConfirmAsync; var acknowledgements = 0;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => { ++acknowledgements; return Task.CompletedTask; };
            var work = new RagBookAcquisitionWork(controller);
            await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => work.RunAsync(files.Request with { Download = true },
                "Book files", new Progress<ManagedModelDownloadProgress>(), default));
            Assert.AreEqual(original.Id, controller.State!.Id); Assert.AreEqual(0, acknowledgements);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }

    [TestMethod]
    public async Task FailedAcquisitionWaitsForUserInsteadOfReportingReady()
    {
        using var files = new Files(); var controller = files.Controller();
        var work = new RagBookAcquisitionWork(controller, _ => new Preparation((_, _) => throw new IOException("offline")));
        await Assert.ThrowsAsync<IOException>(() => work.RunAsync(files.Request, "Book files",
            new Progress<ManagedModelDownloadProgress>(), default));
        Assert.AreEqual(BackgroundOperationPhase.Waiting, controller.State!.Phase);
        Assert.IsTrue(controller.State.RequiresDecision); Assert.IsFalse(controller.IsRunning);
    }

    private sealed class Preparation(Func<string, CancellationToken, Task<ManagedModelArtifactCard>> prepare) : IRagBookModelPreparation
    {
        public IReadOnlyList<ManagedModelArtifactCard> Cards => [];
        public Task<ManagedModelArtifactCard> PrepareAsync(string id, bool download, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
            => prepare(id, token);
        public void Dispose() { }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-rag-background-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(_root, "operation.json");
        public RagBookAcquisitionRequest Request => new(Path.Combine(_root, "models"), 3, [RagBookModels.EmbeddingId], false);
        public BackgroundOperationController Controller() => new(new(StatePath));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
