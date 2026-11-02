using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class EmergencyStopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BackgroundOperationState Input(string title) => new() {
        Kind = "emergency.test", Title = title, Project = "test-project",
        Input = JsonSerializer.SerializeToElement(new { Source = title, Seed = 2048 }),
        Checkpoint = JsonSerializer.SerializeToElement(new { Completed = new[] { "part-1", "part-2" } })
    };

    [TestMethod]
    public async Task StopCancelsRunningPausedAndWaitingButPreservesCheckpointsResultsAndArtifacts()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var completed = Input("Already completed") with { Phase = BackgroundOperationPhase.Completed };
        fixture.Store.Save(completed);
        var resultPath = Path.Combine(fixture.Root, "Results", completed.Id + ".json");
        var resultBefore = File.ReadAllBytes(resultPath);
        var paused = Input("Paused") with { Phase = BackgroundOperationPhase.Paused, UserPaused = true };
        var waiting = Input("Waiting") with { Phase = BackgroundOperationPhase.Waiting, UserPaused = true };
        fixture.Store.Operation(paused.Id).Save(paused); fixture.Store.Operation(waiting.Id).Save(waiting);
        fixture.Store.Save(waiting); var controller = new BackgroundOperationController(fixture.Store); controller.Load();
        var active = Input("Running"); var started = Signal();
        using var lifetime = new CancellationTokenSource();
        var work = controller.RunAsync(active, async token => {
            controller.SaveCheckpoint(new { Completed = new[] { "part-1", "part-2" } }); started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token); return 1;
        }, () => Task.CompletedTask, lifetime.Token);
        try {
            await started.Task.WaitAsync(Timeout);
            var plan = BackgroundEmergencyStop.Capture(controller, registry);
            Assert.HasCount(3, plan.Tasks);
            await BackgroundEmergencyStop.ExecuteAsync(controller, registry, plan, () => Task.CompletedTask).WaitAsync(Timeout);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await work.WaitAsync(Timeout));
            foreach (var state in new[] { paused, waiting, active }) AssertCanceled(fixture, state);
            Assert.IsFalse(controller.HasPending); Assert.IsFalse(controller.BlocksNewWork); Assert.IsFalse(controller.IsRunning);
            CollectionAssert.AreEqual(resultBefore, File.ReadAllBytes(resultPath)); fixture.AssertArtifactUnchanged();
            Assert.AreEqual(42, await controller.RunAsync(Input("New request"), _ => Task.FromResult(42), () => Task.CompletedTask, default));
        }
        finally { lifetime.Cancel(); await ObserveAsync(work); }
    }

    [TestMethod]
    public async Task NewOperationAfterConfirmationRejectsStalePlanBeforeCancelingAnything()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var controller = new BackgroundOperationController(fixture.Store); controller.Load();
        var oldPlan = BackgroundEmergencyStop.Capture(controller, registry); var input = Input("Started after review");
        var started = Signal(); var canceled = false; var releases = 0;
        using var lifetime = new CancellationTokenSource();
        var work = controller.RunAsync(input, async token => {
            using var registration = token.Register(() => canceled = true); started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token); return 1;
        }, () => Task.CompletedTask, lifetime.Token);
        try {
            await started.Task.WaitAsync(Timeout); var before = File.ReadAllBytes(fixture.CheckpointPath);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BackgroundEmergencyStop.ExecuteAsync(
                controller, registry, oldPlan, () => { releases++; return Task.CompletedTask; }));
            Assert.AreEqual("Background.Stop.PlanChanged", error.Message);
            Assert.IsFalse(canceled); Assert.AreEqual(0, releases); Assert.IsFalse(work.IsCompleted);
            Assert.AreEqual(BackgroundOperationPhase.Running, controller.State!.Phase);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.CheckpointPath)); fixture.AssertArtifactUnchanged();
        }
        finally { lifetime.Cancel(); await ObserveAsync(work); }
    }

    [TestMethod]
    public async Task ReplacedAuxiliaryWindowRequiresFreshConfirmation()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var controller = new BackgroundOperationController(fixture.Store); controller.Load();
        EmergencyStopPlan oldPlan;
        using (controller.BeginAuxiliary()) oldPlan = BackgroundEmergencyStop.Capture(controller, registry);
        using (controller.BeginAuxiliary()) {
            var releases = 0;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BackgroundEmergencyStop.ExecuteAsync(
                controller, registry, oldPlan, () => { releases++; return Task.CompletedTask; }));
            Assert.AreEqual("Background.Stop.PlanChanged", error.Message); Assert.AreEqual(0, releases);
            Assert.IsTrue(controller.BlocksNewWork);
        }
        Assert.IsFalse(controller.BlocksNewWork);
    }

    [TestMethod]
    public async Task GateRemainsClosedUntilModelCleanupIsConfirmed()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var input = Input("Needs attention") with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true };
        var controller = fixture.Load(input); var entered = Signal(); var release = Signal();
        var stop = BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
            async () => { entered.SetResult(); await release.Task; });
        try {
            await entered.Task.WaitAsync(Timeout);
            Assert.IsTrue(controller.BlocksNewWork); Assert.IsFalse(stop.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary());
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunAsync<int>(Input("Forbidden"),
                _ => throw new AssertFailedException("Executor entered while cleanup was pending"), () => Task.CompletedTask, default));
            Assert.Throws<InvalidOperationException>(() => registry.Start(MissingCommand(), "test.blocked"));
        }
        finally { release.TrySetResult(); await stop.WaitAsync(Timeout); }
        Assert.IsFalse(controller.BlocksNewWork); AssertCanceled(fixture, input); fixture.AssertArtifactUnchanged();
    }

    [TestMethod]
    public async Task FailedCleanupKeepsBothGatesBlockedUntilSuccessfulEmergencyRetry()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var input = Input("Failed backend") with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true };
        var controller = fixture.Load(input);
        await Assert.ThrowsAsync<IOException>(() => BackgroundEmergencyStop.ExecuteAsync(controller, registry,
            BackgroundEmergencyStop.Capture(controller, registry), () => throw new IOException("Cleanup was not confirmed")));
        Assert.IsTrue(controller.BlocksNewWork); Assert.IsTrue(controller.State!.RequiresDecision);
        Assert.IsTrue(JsonElement.DeepEquals(input.Checkpoint!.Value, controller.State.Checkpoint!.Value));
        Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary());
        Assert.Throws<InvalidOperationException>(() => registry.Start(MissingCommand(), "test.blocked"));
        await BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
            () => Task.CompletedTask).WaitAsync(Timeout);
        Assert.IsFalse(controller.BlocksNewWork); AssertCanceled(fixture, input); fixture.AssertArtifactUnchanged();
    }

    [TestMethod]
    public async Task RetryCannotReleaseGateWhileCanceledAttemptsCleanupIsStillRunning()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var input = Input("Outstanding cleanup") with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true };
        var controller = fixture.Load(input); var oldCleanupEntered = Signal(); var finishOldCleanup = Signal();
        using var cancellation = new CancellationTokenSource();
        var firstStop = BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
            async () => { oldCleanupEntered.SetResult(); await finishOldCleanup.Task; }, cancellation.Token);
        Task? retry = null;
        try {
            await oldCleanupEntered.Task.WaitAsync(Timeout); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await firstStop.WaitAsync(Timeout));
            Assert.IsTrue(controller.BlocksNewWork); Assert.IsFalse(finishOldCleanup.Task.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => registry.Start(MissingCommand(), "test.blocked"));

            var freshCleanupCalls = 0; var retryEntered = Signal();
            controller.Changed += () => { if (controller.IsRunning) retryEntered.TrySetResult(); };
            retry = BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
                () => { Interlocked.Increment(ref freshCleanupCalls); return Task.CompletedTask; });
            await retryEntered.Task.WaitAsync(Timeout);
            var observation = Task.Delay(100);
            Assert.AreSame(observation, await Task.WhenAny(retry, observation), "Retry must wait for the old cleanup callback.");
            Assert.AreEqual(0, freshCleanupCalls); Assert.IsTrue(controller.BlocksNewWork);
            Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary()); fixture.AssertArtifactUnchanged();

            finishOldCleanup.SetResult(); await retry.WaitAsync(Timeout);
            Assert.AreEqual(1, freshCleanupCalls); Assert.IsFalse(controller.BlocksNewWork);
            AssertCanceled(fixture, input); fixture.AssertArtifactUnchanged();
        }
        finally {
            cancellation.Cancel(); finishOldCleanup.TrySetResult(); await ObserveAsync(firstStop);
            if (retry is not null) await ObserveAsync(retry);
        }
    }

    [TestMethod]
    public async Task LateExecutorUnwindCannotReopenGateOrResurrectCanceledCheckpoint()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var controller = new BackgroundOperationController(fixture.Store); var input = Input("Slow unwind");
        var started = Signal(); var cancellationObserved = Signal(); var allowUnwind = Signal(); var cleanup = Signal();
        using var lifetime = new CancellationTokenSource();
        var work = controller.RunAsync<int>(input, async token => {
            controller.SaveCheckpoint(new { Completed = new[] { "part-1", "part-2" } });
            using var registration = token.Register(() => cancellationObserved.TrySetResult()); started.SetResult();
            await allowUnwind.Task;
            throw new InvalidOperationException("Late backend unwind after cancellation");
        }, () => Task.CompletedTask, lifetime.Token);
        Task? stop = null;
        try {
            await started.Task.WaitAsync(Timeout);
            stop = BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
                () => { cleanup.TrySetResult(); return Task.CompletedTask; });
            await cancellationObserved.Task.WaitAsync(Timeout); await cleanup.Task.WaitAsync(Timeout);
            Assert.IsFalse(stop.IsCompleted); Assert.IsTrue(controller.BlocksNewWork);
            Assert.Throws<InvalidOperationException>(() => controller.BeginAuxiliary());
            fixture.AssertArtifactUnchanged();
            allowUnwind.SetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await work.WaitAsync(Timeout));
            await stop.WaitAsync(Timeout); await controller.WaitForAllIdleAsync().WaitAsync(Timeout);
            AssertCanceled(fixture, input); Assert.IsFalse(controller.BlocksNewWork); fixture.AssertArtifactUnchanged();
            await Assert.ThrowsAsync<OperationCanceledException>(() => controller.RunAsync<int>(input,
                _ => throw new AssertFailedException("A canceled execution was resurrected"), () => Task.CompletedTask, default));
            AssertCanceled(fixture, input);
        }
        finally {
            allowUnwind.TrySetResult(); lifetime.Cancel(); await ObserveAsync(work);
            if (stop is not null) await ObserveAsync(stop);
        }
    }

    [TestMethod]
    public async Task StopCancelsRestorerPreparingBeforeItsExecutorStarts()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var input = Input("Preparing restoration") with { Phase = BackgroundOperationPhase.Waiting, UserPaused = true };
        var controller = fixture.Load(input); var entered = Signal(); var canceled = Signal();
        controller.Register(input.Kind, async (_, token) => {
            using var registration = token.Register(() => canceled.TrySetResult()); entered.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });
        var resume = controller.ResumeAsync(input.Id, default);
        await entered.Task.WaitAsync(Timeout);
        await BackgroundEmergencyStop.ExecuteAsync(controller, registry, BackgroundEmergencyStop.Capture(controller, registry),
            () => Task.CompletedTask).WaitAsync(Timeout);
        await canceled.Task.WaitAsync(Timeout); await Assert.ThrowsAsync<OperationCanceledException>(async () => await resume.WaitAsync(Timeout));
        AssertCanceled(fixture, input); Assert.IsFalse(controller.BlocksNewWork); fixture.AssertArtifactUnchanged();
    }

    [TestMethod]
    public async Task RegistryStopsOnlyReviewedOwnedWorkersAndLeavesDictationAndUnregisteredProcessAlive()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        using var external = Process.Start(IdleCommand())!;
        using var worker = registry.Start(IdleCommand(), "test.model");
        using var voice = registry.Start(IdleCommand(), "VoiceInput.Test");
        var controller = new BackgroundOperationController(fixture.Store); controller.Load();
        var plan = BackgroundEmergencyStop.Capture(controller, registry);
        Assert.HasCount(1, plan.Workers); Assert.AreEqual(worker.Id, plan.Workers.Single().Pid);
        try {
            await BackgroundEmergencyStop.ExecuteAsync(controller, registry, plan, () => Task.CompletedTask).WaitAsync(Timeout);
            await worker.WaitForExitAsync().WaitAsync(Timeout);
            Assert.IsFalse(voice.HasExited); Assert.IsFalse(external.HasExited);
            Assert.IsTrue(registry.GetSnapshot().Any(snapshot => snapshot.Pid == voice.Id));
            Assert.IsFalse(registry.GetSnapshot().Any(snapshot => snapshot.Pid == worker.Id));
        }
        finally { registry.Dispose(); await StopOwnProbeAsync(external); }
    }

    [TestMethod]
    public async Task NewWorkerAfterReviewRejectsPlanBeforeCancelingOldWorkerOrTask()
    {
        using var fixture = new Fixture(); using var registry = new OwnedProcessRegistry();
        var input = Input("Waiting review") with { Phase = BackgroundOperationPhase.Waiting, RequiresDecision = true };
        var controller = fixture.Load(input);
        using var first = registry.Start(IdleCommand(), "test.first");
        var plan = BackgroundEmergencyStop.Capture(controller, registry);
        using var added = registry.Start(IdleCommand(), "test.added"); var before = File.ReadAllBytes(fixture.CheckpointPath);
        var releases = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BackgroundEmergencyStop.ExecuteAsync(controller, registry,
            plan, () => { releases++; return Task.CompletedTask; }));
        Assert.AreEqual("Background.Stop.PlanChanged", error.Message); Assert.AreEqual(0, releases);
        Assert.IsFalse(first.HasExited); Assert.IsFalse(added.HasExited);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.CheckpointPath)); fixture.AssertArtifactUnchanged();
    }

    [TestMethod]
    [DataRow("VoiceInput.GigaAm", true)]
    [DataRow("VoiceInput.Whisper", true)]
    [DataRow("VoiceInput", false)]
    [DataRow("test.VoiceInput.GigaAm", false)]
    [DataRow("voiceinput.GigaAm", false)]
    public void DictationExemptionIsAnExactComponentNamespace(string component, bool exempt) =>
        Assert.AreEqual(exempt, OwnedProcessRegistry.IsEmergencyExempt(component));

    private static void AssertCanceled(Fixture fixture, BackgroundOperationState original)
    {
        var saved = fixture.Store.Operation(original.Id).Load()!;
        Assert.AreEqual(BackgroundOperationPhase.Canceled, saved.Phase); Assert.IsTrue(saved.UserPaused);
        Assert.IsFalse(saved.RequiresDecision); Assert.AreEqual(original.Id, saved.Id);
        Assert.IsTrue(JsonElement.DeepEquals(original.Input, saved.Input));
        Assert.IsTrue(JsonElement.DeepEquals(original.Checkpoint!.Value, saved.Checkpoint!.Value));
    }
    private static ProcessStartInfo IdleCommand() => new("cmd.exe") {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "/d", "/c", "ping -n 60 127.0.0.1 >nul" }
    };
    private static ProcessStartInfo MissingCommand() => new("missing-emergency-test-" + Guid.NewGuid().ToString("N") + ".exe") { UseShellExecute = false };
    private static async Task StopOwnProbeAsync(Process process)
    { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(Timeout); }
    private static async Task ObserveAsync(Task task)
    { try { await task.WaitAsync(Timeout); } catch (OperationCanceledException) { } catch (InvalidOperationException) { } }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicProjectTests.Files _files = new();
        private readonly byte[] _artifact = [1, 3, 5, 7, 9];
        public string Root => _files.Root;
        public string CheckpointPath => Path.Combine(Root, "operation.json");
        public BackgroundOperationStore Store => new(CheckpointPath);
        public Fixture() { Directory.CreateDirectory(Root); File.WriteAllBytes(Path.Combine(Root, "completed-part.mp4"), _artifact); }
        public BackgroundOperationController Load(BackgroundOperationState state)
        { Store.Save(state); var controller = new BackgroundOperationController(Store); controller.Load(); return controller; }
        public void AssertArtifactUnchanged() => CollectionAssert.AreEqual(_artifact, File.ReadAllBytes(Path.Combine(Root, "completed-part.mp4")));
        public void Dispose() => _files.Dispose();
    }
}
