using AIHub.Models;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartWorkTests
{
    private string _root = "";
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "LOPATA-smart-work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private async Task<BookSmartWorkInput> Input(int count = 2)
    {
        var sources = Enumerable.Range(1, count).Select(index => Path.Combine(_root, index + ".txt")).ToArray();
        foreach (var source in sources) await File.WriteAllTextAsync(source, "Original author text " + Path.GetFileName(source));
        return await BookSmartWork.CaptureAsync(new() { Sources = sources, OutputFormat = "html", Smart = new(), OutputDirectory = Path.Combine(_root, "out") },
            Path.Combine(_root, "models"), Path.Combine(_root, "core.gguf"), "ru", CancellationToken.None);
    }
    [TestMethod]
    public async Task CancelBetweenGroupsReplaysCompletedReceiptAndRetainsActualOrder()
    {
        var input = await Input(); var calls = new Dictionary<string, int>(); var disposed = 0; var interrupt = true;
        IBookSmartProcessor Factory(string cache)
        {
            Assert.IsTrue(cache.StartsWith(Path.Combine(_root, "work"), StringComparison.OrdinalIgnoreCase));
            return new FakeProcessor((content, source, _) =>
            {
                calls[source] = calls.GetValueOrDefault(source) + 1;
                if (interrupt && source == input.Request.Sources[1]) throw new OperationCanceledException();
                return Task.FromResult(new BookSmartProcessed(content, [], []));
            }, () => disposed++);
        }
        var work = new BookSmartWork(Path.Combine(_root, "work"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.RunAsync(input, Factory, null, null, CancellationToken.None));
        Assert.AreEqual(1, Directory.GetFiles(input.Request.OutputDirectory).Length);
        interrupt = false;
        var result = await work.RunAsync(input, Factory, null, null, CancellationToken.None);
        Assert.AreEqual(0, result.Failures.Length); Assert.AreEqual(2, result.Outputs.Length); Assert.AreEqual(2, disposed);
        Assert.AreEqual(1, calls[input.Request.Sources[0]], "Completed group must not run the model twice.");
        Assert.AreEqual(2, calls[input.Request.Sources[1]]);
        CollectionAssert.AreEqual(new[] { "1.html", "2.html" }, result.Outputs.Select(Path.GetFileName).ToArray());
        Assert.AreEqual(2, Directory.GetFiles(input.Request.OutputDirectory).Length);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task ExitAfterAtomicMoveRecoversJournalWithoutDuplicateOutputOrReview(bool interruptedBeforeMove)
    {
        var input = await Input(1); var calls = 0; var reviews = 0; using var stop = new CancellationTokenSource();
        IBookSmartProcessor Factory(string _) => new FakeProcessor((content, _, _) =>
        { calls++; return Task.FromResult(new BookSmartProcessed(content, [], [])); });
        Task<BookReviewDecision> Review(BookReviewSession _, CancellationToken token) { reviews++; return Task.FromResult(BookReviewDecision.Save); }
        var work = new BookSmartWork(Path.Combine(_root, "work"));
        var progress = new DirectProgress(value => { if (value.Phase == "completed") stop.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => work.RunAsync(input, Factory, Review, progress, stop.Token));
        var before = Directory.GetFiles(input.Request.OutputDirectory).Single(); var originalBytes = File.ReadAllBytes(before);
        if (interruptedBeforeMove)
        {
            using var journal = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "work", "runs", input.Id, "0", "publication.json")));
            File.Move(before, journal.RootElement.GetProperty("Pending").GetString()!);
        }
        var replayed = new List<BookConversionProgress>();
        var result = await work.RunAsync(input, Factory, Review, new DirectProgress(replayed.Add), CancellationToken.None);
        Assert.AreEqual(1, calls); Assert.AreEqual(1, reviews); Assert.AreEqual(before, result.Outputs.Single());
        Assert.AreEqual(1, Directory.GetFiles(input.Request.OutputDirectory).Length); CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(before));
        Assert.AreEqual(before, replayed.Single(value => value.Phase == "completed").OutputPath);
    }

    [TestMethod]
    public async Task ResumeRejectsChangedSourceAndChangedPublishedBytesBeforeCallingModels()
    {
        var input = await Input(1); var calls = 0;
        IBookSmartProcessor Factory(string _) => new FakeProcessor((content, _, _) => { calls++; return Task.FromResult(new BookSmartProcessed(content, [], [])); });
        var work = new BookSmartWork(Path.Combine(_root, "work"));
        var result = await work.RunAsync(input, Factory, null, null, CancellationToken.None);
        var source = input.Request.Sources[0]; var original = await File.ReadAllBytesAsync(source);
        await File.WriteAllTextAsync(source, "Changed after pause");
        var error = await Assert.ThrowsAsync<IOException>(() => work.RunAsync(input, Factory, null, null, CancellationToken.None));
        StringAssert.Contains(error.Message, "SourceChanged"); Assert.AreEqual(1, calls);
        await File.WriteAllBytesAsync(source, original); await File.AppendAllTextAsync(result.Outputs[0], "tampered");
        error = await Assert.ThrowsAsync<IOException>(() => work.RunAsync(input, Factory, null, null, CancellationToken.None));
        StringAssert.Contains(error.Message, "OutputChanged"); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task WaitingPassesToControllerAndSameRunCanResume()
    {
        var input = await Input(1); var wait = true;
        IBookSmartProcessor Factory(string _) => new FakeProcessor((content, _, _) =>
        {
            if (wait) throw new BackgroundOperationWaitingException("Preparation required");
            return Task.FromResult(new BookSmartProcessed(content, [], []));
        });
        var work = new BookSmartWork(Path.Combine(_root, "work"));
        await Assert.ThrowsAsync<BackgroundOperationWaitingException>(() => work.RunAsync(input, Factory, null, null, CancellationToken.None));
        wait = false; var result = await work.RunAsync(input, Factory, null, null, CancellationToken.None);
        Assert.AreEqual(1, result.Outputs.Length); Assert.AreEqual(0, result.Failures.Length);
    }

    [TestMethod]
    public async Task FailedGroupDoesNotProduceASuccessNoticeAndRetainsPartialResults()
    {
        var input = await Input();
        IBookSmartProcessor Factory(string _) => new FakeProcessor((content, source, _) =>
        {
            if (source == input.Request.Sources[1]) throw new InvalidDataException("BookConverter.Smart.InvalidOcr");
            return Task.FromResult(new BookSmartProcessed(content, [], []));
        });
        var work = new BookSmartWork(Path.Combine(_root, "work"));
        await Assert.ThrowsAsync<BackgroundOperationFailedException>(() => work.RunAsync(input, Factory, null, null, CancellationToken.None));
        var partial = work.LoadResult(input); Assert.IsNotNull(partial);
        Assert.AreEqual(1, partial.Outputs.Length); Assert.AreEqual(1, partial.Failures.Length);
        Assert.IsTrue(File.Exists(partial.Outputs[0]));
    }

    private sealed class FakeProcessor(Func<BookContent, string, CancellationToken, Task<BookSmartProcessed>> execute, Action? dispose = null) : IBookSmartProcessor, IDisposable
    {
        public Task<BookSmartProcessed> ProcessAsync(BookContent content, string source, CancellationToken token) => execute(content, source, token);
        public void Dispose() => dispose?.Invoke();
    }
    private sealed class DirectProgress(Action<BookConversionProgress> action) : IProgress<BookConversionProgress>
    { public void Report(BookConversionProgress value) => action(value); }
}
