using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniContextRecoveryTests
{
    private const string Valid = "{\"title\":\"Title\",\"paragraphs\":[\"Preserved result\"],\"review_items\":[],\"uncertainties\":[]}";

    [TestMethod]
    public void Admission_ReservesAnswerBefore95Percent_AndHandlesLargeInputs()
    {
        var boundary = OmniContextBudget.Boundary(32768);
        Assert.AreEqual(4097, OmniContextBudget.OutputBudget(boundary - 4097, 32768));
        Assert.Throws<ImageAnalysisContextExhaustedException>(() => OmniContextBudget.OutputBudget(boundary - 4096, 32768));
        Assert.Throws<ImageAnalysisContextExhaustedException>(() => OmniContextBudget.OutputBudget(boundary, 32768));
        Assert.Throws<ImageAnalysisContextExhaustedException>(() => OmniContextBudget.OutputBudget(int.MaxValue, 32768));
    }

    [TestMethod]
    public async Task Probe_UsesActualTemplateAndTokens_WithEveryImageAndThinkingPrefix()
    {
        var requests = new List<(string Path, string Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            requests.Add((request.RequestUri!.AbsolutePath, body));
            return request.RequestUri.AbsolutePath == "/apply-template"
                ? "{\"prompt\":\"<|user|>Точный промпт<|assistant|><think>\"}"
                : "{\"tokens\":[1,2,3,4,5]}";
        }));
        var conversation = new ImageAnalysisHiddenMessage[]
        {
            new() { Role = "user", Content = "Точный промпт", IncludesImage = true },
            new() { Role = "assistant", Content = "Saved observation" },
            new() { Role = "user", Content = "Same image again", IncludesImage = true }
        };
        var count = await OmniLlamaContextProbe.MeasureAsync(http, new Uri("http://localhost/"), conversation,
            "data:image/png;base64,YQ==", default);
        Assert.AreEqual(8197, count);
        using var first = JsonDocument.Parse(requests[0].Body);
        Assert.IsTrue(first.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.AreEqual(3, first.RootElement.GetProperty("messages").GetArrayLength());
        using var second = JsonDocument.Parse(requests[1].Body);
        Assert.AreEqual("<|user|>Точный промпт<|assistant|><think>", second.RootElement.GetProperty("content").GetString());
        Assert.IsTrue(second.RootElement.GetProperty("add_special").GetBoolean());
    }

    [TestMethod]
    public async Task Recovery_ThirdAttemptSucceeds_WithoutRerunningObservationOrPollutingHistory()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var storage = Storage(root);
            var runtime = new ScriptedRuntime(["Observation", "broken", "broken again", Valid]);
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            var session = Session();
            var stages = new List<string>();
            var result = await pipeline.CreateAsync(session.File!, session.Settings, storage, session, _ => { },
                new InlineProgress(p => stages.Add(p.Stage)), null, c => session.VisualReport = c.VisualReport, default);
            CollectionAssert.AreEqual(new[] { "analyze", "compose", "compose", "compose" }, runtime.Stages);
            Assert.AreEqual("Observation", session.VisualReport);
            Assert.AreEqual(4, session.HiddenConversation.Count);
            Assert.AreEqual(4, result.HiddenConversation!.Count);
            Assert.AreEqual(2, stages.Count(s => s == OmniResponseRecovery.WaitingStage));
            foreach (var request in runtime.Conversations.Skip(1))
            {
                Assert.AreEqual(3, request.Count);
                Assert.AreEqual("Observation", request[1].Content);
                Assert.IsFalse(request.Any(m => m.Content.StartsWith("broken")));
            }
            Assert.AreEqual(4, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Count(p => p.Contains("OmniResponses")));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task Revision_ThreeFailuresPreservePreviousConversationAndVersions()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var session = Session();
            session.VisualReport = "Observation";
            session.HiddenConversation = [new() { Role = "user", Content = "Look", IncludesImage = true },
                new() { Role = "assistant", Content = "Observation" }, new() { Role = "user", Content = "Compose" },
                new() { Role = "assistant", Content = Valid }];
            session.Versions.Add(new() { Text = "Previous description" });
            var before = JsonSerializer.Serialize(session.HiddenConversation);
            var runtime = new ScriptedRuntime(["bad1", "bad2", "bad3", Valid]);
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            await Assert.ThrowsAsync<ImageAnalysisOmniFormatException>(() => pipeline.ReviseAsync(session, "Shorter", Storage(root), _ => { }, null, null, default));
            Assert.AreEqual(3, runtime.Stages.Count);
            Assert.AreEqual(before, JsonSerializer.Serialize(session.HiddenConversation));
            Assert.AreEqual("Previous description", session.Versions.Single().Text);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task ContextFailure_PersistsBlock_PreservesResults_AndPreventsReopenedRequests()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var storage = Storage(root);
            var session = Session();
            session.Versions.Add(new() { Text = "Previous description" });
            var runtime = new ScriptedRuntime([]) { Failure = new ImageAnalysisContextExhaustedException("95%") };
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => pipeline.CreateAsync(session.File!, session.Settings, storage, session, _ => { }, null, null, null, default));
            Assert.AreEqual(1, runtime.Stages.Count);
            Assert.IsTrue(session.ContextBlocked);
            var store = new ImageAnalysisSessionStore(); store.Save(session, storage);
            var loaded = store.Load(session.SessionId, storage)!;
            Assert.IsTrue(loaded.ContextBlocked);
            Assert.AreEqual("Previous description", loaded.Versions.Single().Text);
            await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => pipeline.CreateAsync(loaded.File!, loaded.Settings, storage, loaded, _ => { }, null, null, null, default));
            Assert.AreEqual(1, runtime.Stages.Count);
            Assert.IsFalse(new ImageAnalysisLiterarySession().ContextBlocked);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task CompletedObservationAtBoundary_IsSavedButComposeIsNotSent()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var runtime = new ScriptedRuntime(["Observation", Valid]) { OutputTokens = OmniContextBudget.Boundary(32768) - 10 };
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            var session = Session();
            await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => pipeline.CreateAsync(session.File!, session.Settings, Storage(root), session,
                _ => { }, null, null, c => session.VisualReport = c.VisualReport, default));
            Assert.AreEqual("Observation", session.VisualReport);
            Assert.IsTrue(session.ContextBlocked);
            CollectionAssert.AreEqual(new[] { "analyze" }, runtime.Stages);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task Recovery_DoesNotRetryCancellationContextOrRuntimeFailures()
    {
        foreach (var error in new Exception[] { new OperationCanceledException(), new ImageAnalysisContextExhaustedException("full"), new InvalidOperationException("CUDA OOM") })
        {
            var attempts = 0;
            try
            {
                await OmniResponseRecovery.RunAsync<int>(_ => { attempts++; throw error; }, "compose", _ => { }, null, default);
                Assert.Fail("Expected failure");
            }
            catch (Exception actual) { Assert.AreSame(error, actual); }
            Assert.AreEqual(1, attempts);
        }
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => OmniResponseRecovery.RunAsync<int>(_ =>
        {
            calls++; cts.Cancel(); throw new ImageAnalysisOmniFormatException(new InvalidDataException());
        }, "compose", _ => { }, null, cts.Token));
        Assert.AreEqual(1, calls);
    }

    private static StorageSettings Storage(string root)
    { var storage = new StorageSettings(); storage.Results.Locations.Add(new() { Path = root }); return storage; }
    private static ImageAnalysisLiterarySession Session() => new()
    {
        File = new() { SourcePath = "test.png" },
        ModelId = ManagedModelCatalog.Qwen25OmniRepository, ModelRevision = ManagedModelCatalog.Qwen25OmniRevision
    };
    private sealed class InlineProgress(Action<ImageAnalysisLiteraryProgress> callback) : IProgress<ImageAnalysisLiteraryProgress>
    { public void Report(ImageAnalysisLiteraryProgress value) => callback(value); }
    private sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await respond(request), Encoding.UTF8, "application/json") };
    }
    private sealed class ScriptedRuntime(IEnumerable<string> answers) : IOmniTextRuntime
    {
        private readonly Queue<string> _answers = new(answers);
        public List<string> Stages { get; } = [];
        public List<List<ImageAnalysisHiddenMessage>> Conversations { get; } = [];
        public Exception? Failure { get; init; }
        public int OutputTokens { get; init; } = 10;
        public bool IsReady => true;
        public string RuntimeVersion => "test";
        public string DeviceMapJson => "{}";
        public ImageAnalysisHeavyResourcePlan? CurrentPlan => null;
        public Task<OmniWarmupResult> PrepareAsync(Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken token, bool reuseCurrentPlan = false) => throw new NotSupportedException();
        public Task<OmniTextGenerationResult> GenerateAsync(string command, string imagePath, IReadOnlyList<ImageAnalysisHiddenMessage> conversation, IProgress<ModelStreamChunk>? streamProgress, CancellationToken token, Action<string>? responseReceived = null, Action<string>? diagnosticReceived = null)
        {
            Stages.Add(command); Conversations.Add(conversation.ToList());
            if (Failure is not null) throw Failure;
            var content = _answers.Dequeue(); responseReceived?.Invoke(content);
            return Task.FromResult(new OmniTextGenerationResult(content, 1, 10, OutputTokens, 32768, "eos", 0, 1, 1, 10, 0, "test", "test"));
        }
        public Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public void Stop() { }
        public void Dispose() { }
    }
}
