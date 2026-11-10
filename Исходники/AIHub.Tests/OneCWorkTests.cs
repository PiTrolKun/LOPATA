using System.IO;
using System.Text.Json;
using AIHub.Models.OneC;
using AIHub.Services.OneC;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OneCWorkTests
{
    private string _root = "";
    private OneCProjectStore _store = null!;
    [TestInitialize] public void Start() { _root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Tests", Guid.NewGuid().ToString("N")); _store = new(_root); }
    [TestCleanup] public void Finish()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    [TestMethod] public async Task ResumeAfterModelStageReusesPersistedVariant()
    {
        var (p, job) = Job(); var model = new Model();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new OneCWork(_store, () => model, new InterruptedCheck()).RunAsync(p.Id, job.Id, "Test", null, default));
        var interrupted = _store.Load(p.Id); Assert.HasCount(1, interrupted.Proposals); Assert.AreEqual("interrupted", interrupted.Pending!.State);
        var id = await new OneCWork(_store, () => model, new Check(false)).RunAsync(p.Id, job.Id, "Test", null, default);
        var loaded = _store.Load(p.Id); Assert.AreEqual(1, model.Calls); Assert.HasCount(1, loaded.Proposals);
        Assert.AreEqual(id, loaded.Proposals[0].Id); Assert.AreEqual("clear", loaded.Proposals[0].Check!.State); Assert.IsNull(loaded.Pending);
    }
    [TestMethod] public async Task CompletedJobIsNotGeneratedAgain()
    {
        var (p, job) = Job(); var model = new Model(); var work = new OneCWork(_store, () => model, new Check(false));
        var first = await work.RunAsync(p.Id, job.Id, "Test", null, default);
        var second = await work.RunAsync(p.Id, job.Id, "Test", null, default);
        Assert.AreEqual(first, second); Assert.AreEqual(1, model.Calls);
    }
    [TestMethod] public async Task AutomaticStaticRepairPreservesVariantsAndStopsAtLimit()
    {
        var (p, job) = Job(); var model = new Model();
        await new OneCWork(_store, () => model, new Check(true)).RunAsync(p.Id, job.Id, "Test", null, default);
        var loaded = _store.Load(p.Id);
        Assert.AreEqual(3, model.Calls); Assert.HasCount(3, loaded.Proposals); Assert.IsNull(loaded.Pending);
        Assert.AreEqual(loaded.Proposals[0].Id, loaded.Proposals[1].ParentProposalId);
        Assert.AreEqual(loaded.Proposals[1].Id, loaded.Proposals[2].ParentProposalId);
        Assert.IsTrue(loaded.Proposals.All(v => v.Check!.Diagnostics.Any(d => d.Severity == "Error")));
    }
    [TestMethod] public async Task CheckerFailureIsNotReportedAsSuccessfulCheck()
    {
        var (p, job) = Job(); var model = new Model();
        await new OneCWork(_store, () => model, new BrokenCheck()).RunAsync(p.Id, job.Id, "Test", null, default);
        var loaded = _store.Load(p.Id); Assert.AreEqual("failed", loaded.Proposals[0].Check!.State); Assert.AreEqual(1, model.Calls);
        Assert.IsTrue(loaded.Proposals[0].Code.Length > 0);
    }
    private (OneCProject, OneCJob) Job()
    {
        var p = _store.Create("Test"); p.Task.Prompt = "Напиши функцию сложения";
        var job = OneCProtocol.CreateJob(p, "fixture-model", "ru"); p.Pending = job; _store.Save(p); return (p, job);
    }
    private sealed class Model : IOneCModelSession
    {
        internal int Calls;
        public Task<OneCModelResponse> GenerateAsync(OneCJob job, CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            return Task.FromResult(new OneCModelResponse(JsonSerializer.Serialize(new { Explanation = "Пример", Code = "А = 1;", SuggestedName = "Пример.bsl", InsertLocation = "Общий модуль", MissingContext = Array.Empty<string>() }), []));
        }
        public void Dispose() { }
    }
    private sealed class Check(bool error) : IOneCCodeChecker
    {
        public string Id => "fixture.check";
        public Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token) => Task.FromResult(new OneCCheck(
            OneCProjectStore.Hash(code), Id, "test", error ? "issues" : "clear", error ? [new("ParseError", "Error", "Fixture syntax error", 1, 1, 1, 2)] : [], DateTimeOffset.UtcNow));
    }
    private sealed class InterruptedCheck : IOneCCodeChecker
    {
        public string Id => "fixture.interrupted";
        public Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token) => throw new OperationCanceledException();
    }
    private sealed class BrokenCheck : IOneCCodeChecker
    {
        public string Id => "fixture.broken";
        public Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token) => throw new IOException("No JSON report");
    }
}
