using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Lopata.Island;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class IslandLinkTests
{
    [TestMethod]
    public async Task ChatWaitsUntilTheMatchingIslandCoreReleasesItsLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var lease = new Mutex(false, IslandLink.CoreLeaseName(directory), out var first);
        Assert.IsTrue(first);
        var waiting = IslandLink.WaitForCoreRetirementAsync(directory, deadline.Token);
        Assert.IsFalse(waiting.IsCompleted);
        Assert.IsTrue(IslandLink.WaitForCoreRetirementAsync(directory + "-other", deadline.Token).IsCompletedSuccessfully);
        using var cancelled = new CancellationTokenSource();
        var interrupted = IslandLink.WaitForCoreRetirementAsync(directory, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted);
        lease.Dispose();
        await waiting;
    }

    [TestMethod]
    public async Task PipeCarriesRussianDraftAndRejectsOversizedFrames()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IslandRequest? received = null;
        using var server = new IslandHostServer((request, _) =>
        {
            received = request;
            return Task.FromResult(new IslandResponse("transferred"));
        });
        var reply = await IslandLink.AskHostAsync(AppContext.BaseDirectory,
            new("draft", Guid.NewGuid().ToString("N"), "Привет.\r\nЭто черновик."), deadline.Token);
        Assert.AreEqual("transferred", reply.Result);
        Assert.AreEqual("Привет.\r\nЭто черновик.", received?.Text);
        using var oversized = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            IslandLink.ReadAsync<IslandRequest>(oversized, deadline.Token));
    }

    [TestMethod]
    public async Task TransferWaitsForRetirementAndRetriesOnlyTheChosenOperation()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var operations = directory.Controller;
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retireDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<string>();
        var link = new IslandHostCommands(operations, async id => { await operations.ResumeAsync(id, deadline.Token); return true; },
            (text, _) => { delivered.Add(text); return Task.FromResult("transferred"); });
        var pending = Job("render");
        int attempts = 0;
        var run = operations.RunAsync(pending, async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                operations.SaveCheckpoint("first frame");
                await Task.Delay(Timeout.Infinite, token);
            }
            Assert.AreEqual("first frame", operations.FindIslandOperation(pending.Id)?.Checkpoint?.GetString());
            return 27;
        }, () => { retiring.TrySetResult(); return retireDone.Task; }, deadline.Token);
        try
        {
            var draft = new IslandRequest("draft", Guid.NewGuid().ToString("N"), "Текст из острова");
            var busy = await link.HandleAsync(draft, deadline.Token);
            Assert.AreEqual("busy", busy.Result);
            Assert.AreEqual(true, busy.ModelActive);
            Assert.IsNull(busy.ModelFailure);
            Assert.AreEqual(pending.Id, busy.BlockingId);
            Assert.IsTrue(busy.Jobs!.Single().CanPause);
            Assert.IsFalse(busy.Jobs!.Single().CanResume);
            var wrong = await link.HandleAsync(draft with { PauseId = Guid.NewGuid().ToString("N") }, deadline.Token);
            Assert.AreEqual("busy", wrong.Result);
            Assert.IsEmpty(delivered);

            var confirm = link.HandleAsync(draft with { PauseId = pending.Id }, deadline.Token);
            await retiring.Task.WaitAsync(deadline.Token);
            Assert.IsFalse(confirm.IsCompleted);
            Assert.IsEmpty(delivered);
            retireDone.SetResult();
            var accepted = await confirm;
            Assert.AreEqual("transferred", accepted.Result);
            Assert.AreEqual(false, accepted.ModelActive);
            Assert.IsNull(accepted.ModelFailure);
            Assert.AreEqual("Paused", accepted.Jobs!.Single().Phase);
            Assert.IsTrue(accepted.Jobs!.Single().CanResume);
            Assert.AreEqual("transferred", (await link.HandleAsync(draft, deadline.Token)).Result);
            CollectionAssert.AreEqual(new[] { "Текст из острова" }, delivered);
            Assert.AreEqual("ok", (await link.HandleAsync(new("resume", pending.Id), deadline.Token)).Result);
            Assert.AreEqual(27, await run.WaitAsync(deadline.Token));
            Assert.AreEqual(2, attempts);
        }
        finally
        {
            retireDone.TrySetResult();
            await deadline.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task SnapshotIncludesMultipleJobsButOnlyExposesCommandsCurrentlyAvailable()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var operations = directory.Controller;
        var link = new IslandHostCommands(operations, _ => Task.FromResult(false), (_, _) => Task.FromResult("invalid"),
            state => state.Kind == "image" ? 0.4 : null);
        var first = Job("music");
        var second = Job("image");
        Task<int> Run(BackgroundOperationState job) => operations.RunAsync(job, async token =>
        { await Task.Delay(Timeout.Infinite, token); return 0; }, () => Task.CompletedTask, deadline.Token);
        var music = Run(first);
        Task<int>? images = null;
        try
        {
            Assert.AreEqual("ok", (await link.HandleAsync(new("pause", first.Id), deadline.Token)).Result);
            images = Run(second);
            var current = await link.HandleAsync(new("status"), deadline.Token);
            Assert.HasCount(2, current.Jobs!);
            Assert.IsTrue(current.Jobs!.Single(job => job.Id == second.Id).CanPause);
            Assert.AreEqual(0.4, current.Jobs!.Single(job => job.Id == second.Id).Progress);
            Assert.IsFalse(current.Jobs!.Single(job => job.Id == first.Id).CanResume);
            Assert.AreEqual(second.Id, current.BlockingId);
        }
        finally
        {
            await deadline.CancelAsync();
            try { await music; } catch (OperationCanceledException) { }
            if (images is not null) try { await images; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task OnlyAConfirmedExecutionErrorGetsAStableMascotSignal()
    {
        using var directory = new TestDirectory();
        var operations = directory.Controller;
        var link = new IslandHostCommands(operations, _ => Task.FromResult(false), (_, _) => Task.FromResult("invalid"));
        Task<int> Run(BackgroundOperationState job, Exception error) =>
            operations.RunAsync(job, _ => Task.FromException<int>(error), () => Task.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() =>
            Run(Job("waiting"), new BackgroundOperationWaitingException("Waiting for input")));
        Assert.IsNull((await link.HandleAsync(new("status"), CancellationToken.None)).ModelFailure);
        operations.DiscardPending();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(Job("failed"), new InvalidOperationException("Local fixture error")));
        var failed = await link.HandleAsync(new("status"), CancellationToken.None);
        Assert.AreEqual(false, failed.ModelActive);
        Assert.IsTrue(Guid.TryParseExact(failed.ModelFailure, "N", out _));
        Assert.AreEqual(failed.ModelFailure, (await link.HandleAsync(new("status"), CancellationToken.None)).ModelFailure);
    }

    private static BackgroundOperationState Job(string name) =>
        new() { Kind = name, Title = name, Input = JsonSerializer.SerializeToElement(new { }) };

    private sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("lopata-island-test-");
        internal BackgroundOperationController Controller => new(new(Path.Combine(root.FullName, "operation.json")));
        public void Dispose() => root.Delete(recursive: true);
    }
}
