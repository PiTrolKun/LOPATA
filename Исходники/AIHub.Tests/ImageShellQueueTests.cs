using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageShellQueueTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "lopata-shell-queue-" + Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ImageShellRequest Request(params string[] names) => new(Guid.NewGuid().ToString("N"), ImageShellOperation.Upscale2,
        names.Select(name => Path.Combine(_root, name)).ToArray());

    [TestMethod]
    public void EnqueueIsDurableIdempotentAndRejectsReusedIdentifiers()
    {
        var store = new ImageShellQueueStore(_root);
        var request = Request("one.png", "one.png", "two.png");
        Assert.IsTrue(store.Enqueue(request));
        Assert.IsFalse(store.Enqueue(request));
        Assert.HasCount(2, new ImageShellQueueStore(_root).Load(request.RequestId)!.Items);
        Assert.HasCount(1, store.Pending());
        Assert.Throws<InvalidDataException>(() => store.Enqueue(request with { Operation = ImageShellOperation.WebP }));
        Assert.Throws<InvalidDataException>(() => store.Enqueue(request with { RequestId = ".." }));
        Assert.Throws<InvalidDataException>(() => store.Enqueue(Request("x") with { Paths = ["relative.png"] }));
    }

    [TestMethod]
    public async Task FailuresRetryAndDoNotBlockFollowingFiles()
    {
        var store = new ImageShellQueueStore(_root); var request = Request("bad.png", "good.png"); store.Enqueue(request);
        var calls = new Dictionary<string, int>();
        var runner = new ImageShellQueueRunner(store, (_, path, _, _) =>
        {
            calls[path] = calls.GetValueOrDefault(path) + 1;
            if (path.EndsWith("bad.png", StringComparison.Ordinal)) throw new IOException("unreadable");
            return Task.FromResult(path);
        });
        await runner.RunAsync(store.Load(request.RequestId)!, CancellationToken.None);
        var done = store.Load(request.RequestId)!;
        Assert.IsTrue(done.Finished); Assert.AreEqual(4, calls[request.Paths[0]]); Assert.AreEqual(1, calls[request.Paths[1]]);
        Assert.AreEqual(ImageUtilityItemStatus.Failed, done.Items[0].Status);
        Assert.AreEqual(ImageUtilityItemStatus.Completed, done.Items[1].Status);
        await runner.RunAsync(done, CancellationToken.None);
        Assert.AreEqual(1, calls[request.Paths[1]]); Assert.IsEmpty(store.Pending());
    }

    [TestMethod]
    public async Task PauseKeepsFinishedItemsAndDoesNotConsumeRetry()
    {
        var store = new ImageShellQueueStore(_root); var request = Request("one.png", "two.png"); store.Enqueue(request);
        using var cancel = new CancellationTokenSource(); var completedCalls = 0;
        var runner = new ImageShellQueueRunner(store, (_, path, _, token) =>
        {
            if (path == request.Paths[0]) { completedCalls++; return Task.FromResult(path); }
            cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(path);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(store.Load(request.RequestId)!, cancel.Token));
        var restored = store.Load(request.RequestId)!;
        Assert.AreEqual(ImageUtilityItemStatus.Completed, restored.Items[0].Status);
        Assert.AreEqual(0, restored.Items[1].Attempts);
        runner = new(store, (_, path, _, _) => { if (path == request.Paths[0]) completedCalls++; return Task.FromResult(path); });
        await runner.RunAsync(restored, CancellationToken.None);
        Assert.AreEqual(1, completedCalls); Assert.IsTrue(store.Load(request.RequestId)!.Finished);
    }

    [TestMethod]
    public void LeasePreventsTwoInstallationsFromReplacingOneFileTwice()
    {
        var first = new ImageShellQueueStore(_root); var second = new ImageShellQueueStore(_root);
        var request = Request("one.png"); first.Enqueue(request);
        using (var lease = first.TryLease(request.RequestId))
        { Assert.IsNotNull(lease); Assert.IsNull(second.TryLease(request.RequestId)); }
        using var released = second.TryLease(request.RequestId); Assert.IsNotNull(released);
    }

    [TestMethod]
    public async Task DamagedFirstInboxEntryDoesNotBlockTheNextValidRequest()
    {
        var store = new ImageShellQueueStore(_root);
        Directory.CreateDirectory(_root);
        var damagedId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_root, damagedId + ".json"), "{\"Finished\":false,\"CreatedAt\":\"2000-01-01T00:00:00Z\"}");
        var valid = Request("next.png"); store.Enqueue(valid);
        Assert.Throws<InvalidDataException>(() => store.Load(damagedId));
        var pending = store.Pending();
        Assert.HasCount(1, pending);
        Assert.AreEqual(valid.RequestId, pending[0].Request.RequestId);
        var calls = 0;
        await new ImageShellQueueRunner(store, (_, path, _, _) => { calls++; return Task.FromResult(path); })
            .RunAsync(pending[0], CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(store.Load(valid.RequestId)!.Finished);
        Assert.IsEmpty(store.Pending());
    }

    [TestMethod]
    public void StructurallyInvalidQueueStatesAreRejectedBeforeProcessing()
    {
        var store = new ImageShellQueueStore(_root);
        var request = Request("one.png"); store.Enqueue(request);
        var original = File.ReadAllText(Path.Combine(_root, request.RequestId + ".json"));
        void Reject(Action<ImageShellJob> change)
        {
            var job = System.Text.Json.JsonSerializer.Deserialize<ImageShellJob>(original)!;
            change(job); store.Save(job);
            Assert.Throws<InvalidDataException>(() => store.Load(request.RequestId));
        }
        Reject(job => job.Items = null!);
        Reject(job => job.Items[0] = null!);
        Reject(job => job.Items[0].Path = Path.Combine(_root, "outside-request.png"));
        Reject(job => job.Items[0].Attempts = -1);
        Reject(job => job.Items[0].Status = (ImageUtilityItemStatus)999);
        Reject(job => job.Items[0].Status = ImageUtilityItemStatus.Failed);
        Reject(job => job.Finished = true);
        Reject(job => { job.Items[0].Status = ImageUtilityItemStatus.Completed; job.Items[0].Attempts = 1; });
    }
}
