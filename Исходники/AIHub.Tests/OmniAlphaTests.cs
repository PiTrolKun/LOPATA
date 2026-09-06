using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniAlphaTests
{
    [TestMethod]
    public void Manifest_ContainsOnlyPinnedModelAndMatchingProjector()
    {
        var card = ManagedModelCatalog.CreateOmniAlpha(@"C:\models");
        Assert.AreEqual(3_833_849_728L, card.TotalBytes);
        Assert.HasCount(2, card.Files);
        Assert.AreEqual("Apache-2.0", card.License);
        Assert.AreEqual("qwen35", card.Architecture);
        CollectionAssert.AreEquivalent(new[] { "Qwen3.8-4B-Distill.Q5_K_M.gguf", "Qwen3.8-4B-Distill.mmproj-f16.gguf" }, card.Files.Select(f => f.RelativePath).ToArray());
        Assert.IsTrue(card.Files.All(f => f.SourceUrl.Contains(ManagedModelCatalog.OmniAlphaRevision) && f.Sha256.Length == 64));
        Assert.AreNotEqual(ManagedModelCatalog.CreateQwen25OmniHeavy(@"C:\models").InstallDirectory, card.InstallDirectory);
        var alpha = ImageAnalysisBundleCatalog.Create().Single(b => b.Id == "light");
        Assert.IsTrue(alpha.IsAvailable);
        Assert.AreEqual(16d, alpha.Requirements.RamGb);
        Assert.AreEqual(8d, alpha.Requirements.VramGb);
        Assert.HasCount(1, alpha.Components);
    }

    [TestMethod]
    public void Installation_AlphaNeverAcquiresLegacyComponents()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            using var service = new ImageAnalysisBundleInstallationService(new ManagedModelLibraryStore(Path.Combine(root, "library")));
            var settings = new StorageSettings();
            settings.Models.Locations.Add(new() { Path = Path.Combine(root, "models") });
            var snapshot = service.Check(settings, "light");
            Assert.HasCount(1, snapshot.Components);
            Assert.AreEqual(ManagedModelCatalog.OmniAlphaArtifactId, snapshot.Components[0].ModelArtifactId);
            Assert.IsFalse(snapshot.CanStart);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public void Protocol_PreservesImageAndExactFirstExchangeInSecondRequest()
    {
        var messages = new ImageAnalysisHiddenMessage[]
        {
            new() { Role = "user", Content = "Мой точный промпт", IncludesImage = true },
            new() { Role = "assistant", Content = "Visible signs" },
            new() { Role = "user", Content = "Compose custom text" }
        };
        using var doc = JsonDocument.Parse(OmniLlamaProtocol.BuildRequest(messages, "data:image/png;base64,YWJj"));
        var turns = doc.RootElement.GetProperty("messages");
        Assert.AreEqual("data:image/png;base64,YWJj", turns[0].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.AreEqual(messages[0].Content, turns[0].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.AreEqual(messages[1].Content, turns[1].GetProperty("content").GetString());
        Assert.AreEqual(messages[2].Content, turns[2].GetProperty("content").GetString());
        var args = OmniLlamaProtocol.Arguments("model", "projector", 12345);
        CollectionAssert.Contains(args, "--no-context-shift");
        CollectionAssert.Contains(args, "--offline");
        Assert.AreEqual("off", args[Array.IndexOf(args, "--fit") + 1]);
        Assert.AreEqual("1", args[Array.IndexOf(args, "-np") + 1]);
        Assert.AreEqual("32768", args[Array.IndexOf(args, "-c") + 1]);
        Assert.AreEqual("deepseek", args[Array.IndexOf(args, "--reasoning-format") + 1]);
        Assert.AreEqual(0.6, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.AreEqual(0.95, doc.RootElement.GetProperty("top_p").GetDouble());
        Assert.AreEqual(20, doc.RootElement.GetProperty("top_k").GetInt32());
        Assert.AreEqual(0, doc.RootElement.GetProperty("min_p").GetInt32());
        Assert.AreEqual(-1, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.IsTrue(doc.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [TestMethod]
    public async Task Stream_AcceptsEosAndUsageAndRetainsRawResponse()
    {
        const string wire = "data: {\"choices\":[{\"delta\":{\"content\":\"Привет\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":5}}\n\ndata: [DONE]\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        string? raw = null;
        var result = await OmniLlamaProtocol.ReadAsync(stream, null, s => raw = s, CancellationToken.None);
        Assert.AreEqual("Привет", result.Content);
        Assert.AreEqual(123, result.InputTokens);
        Assert.AreEqual(5, result.GeneratedTokens);
        Assert.AreEqual("eos", result.FinishReason);
        StringAssert.Contains(raw!, "[DONE]");
    }

    [TestMethod]
    [DataRow("length")]
    [DataRow("stop")]
    public async Task Stream_RejectsTruncationOrMissingDoneAndPreservesEvidence(string reason)
    {
        var wire = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "partial" }, finish_reason = reason } } }) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        string? raw = null;
        if (reason == "length")
            await Assert.ThrowsAsync<ImageAnalysisContextExhaustedException>(() => OmniLlamaProtocol.ReadAsync(stream, null, s => raw = s, CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => OmniLlamaProtocol.ReadAsync(stream, null, s => raw = s, CancellationToken.None));
        StringAssert.Contains(raw!, "partial");
    }

    [TestMethod]
    [DataRow("light")]
    [DataRow("medium")]
    public async Task SharedPipeline_UsesSelectedIdentityCustomPairCheckpointAndRevision(string bundle)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var storage = new StorageSettings();
            storage.Results.Locations.Add(new() { Path = root });
            var runtime = new FakeRuntime(bundle);
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            var settings = new ImageAnalysisLiterarySettings
            {
                LanguageCode = "en", PromptMode = PromptModes.Custom,
                CustomPrompts = OmniPromptPairAdapter.CreateDefault("en") with { Name = "Alpha", AnalysisPrompt = "COUNT THE RED SIGNS", ComposePrompt = "EXPLAIN EACH SIGN" }
            };
            var session = new ImageAnalysisLiterarySession { BundleId = bundle, Settings = settings, File = new() { SourcePath = "test.png" } };
            ImageAnalysisPipelineCheckpoint? checkpoint = null;
            await pipeline.PrepareAsync(storage, session, false, _ => { }, null, CancellationToken.None);
            var result = await pipeline.CreateAsync(session.File, settings, storage, session, _ => { }, null, null, c => checkpoint = c, CancellationToken.None);
            session.VisualReport = result.VisualReport;
            Assert.AreEqual(runtime.PipelineId, session.PipelineId);
            Assert.AreEqual(bundle, session.BundleId);
            Assert.AreEqual(runtime.ModelRevision, session.ModelRevision);
            Assert.AreEqual(2, checkpoint!.HiddenConversation.Count);
            Assert.AreEqual(3, runtime.Requests[1].Count);
            StringAssert.Contains(runtime.Requests[1][0].Content, "COUNT THE RED SIGNS");
            Assert.AreEqual("There are two red signs.", runtime.Requests[1][1].Content);
            StringAssert.Contains(runtime.Requests[1][2].Content, "EXPLAIN EACH SIGN");
            await pipeline.ReviseAsync(session, "Make it shorter", storage, _ => { }, null, null, CancellationToken.None);
            Assert.AreEqual(5, runtime.Requests[2].Count);
            Assert.AreEqual(6, session.HiddenConversation.Count);
            var store = new ImageAnalysisSessionStore(); store.Save(session, storage);
            var loaded = store.Load(session.SessionId, storage)!;
            Assert.AreEqual(settings.CustomPrompts, loaded.Settings.CustomPrompts);
            Assert.AreEqual(runtime.PipelineId, loaded.PipelineId);
            Assert.AreEqual(3, Directory.GetFiles(store.GetProjectsDirectory(storage), "*.json", SearchOption.AllDirectories).Count(p => p.Contains("OmniResponses")));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task Replacement_PreservesOldWorkAndRejectsContinuationBeforeGeneration()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var storage = new StorageSettings();
            storage.Results.Locations.Add(new() { Path = root });
            var session = new ImageAnalysisLiterarySession
            {
                BundleId = "light", PipelineId = ImageAnalysisPipelineIds.OmniAlpha, PipelineVersion = "1",
                ModelId = "ggml-org/Qwen2.5-Omni-3B-GGUF",
                ModelRevision = "75f1b73b657a50f5092502799457ccb4a4a1f9df",
                RuntimeId = ImageAnalysisRuntimeIds.OmniLlama,
                File = new() { SourcePath = "old.png" }, VisualReport = "Original observation",
                HiddenConversation = [new() { Role = "assistant", Content = "Original answer" }],
                Versions = [new() { Number = 1, Text = "Original description" }]
            };
            var original = JsonSerializer.Serialize(session);
            var runtime = new FakeRuntime();
            using var pipeline = new OmniHeavySingleImageLiteraryPipeline(runtime);
            await pipeline.PrepareAsync(storage, session, false, _ => { }, null, default);
            await Assert.ThrowsAsync<ImageAnalysisModelChangedException>(() => pipeline.CreateAsync(
                session.File, session.Settings, storage, session, _ => { }, null, null, null, default));
            await Assert.ThrowsAsync<ImageAnalysisModelChangedException>(() => pipeline.ReviseAsync(
                session, "Rewrite", storage, _ => { }, null, null, default));
            Assert.HasCount(0, runtime.Requests);
            Assert.AreEqual(original, JsonSerializer.Serialize(session));
            var store = new ImageAnalysisSessionStore();
            store.Save(session, storage);
            var loaded = store.Load(session.SessionId, storage)!;
            Assert.AreEqual(session.ModelId, loaded.ModelId);
            Assert.AreEqual(session.ModelRevision, loaded.ModelRevision);
            Assert.AreEqual(ImageAnalysisRuntimeIds.OmniLlama, loaded.RuntimeId);
            Assert.AreEqual("Original description", loaded.Versions[0].Text);
            Assert.AreEqual("Original answer", loaded.HiddenConversation[0].Content);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public void Replacement_AcceptsNewOrMatchingSessionsButNotUnattributedOldAnswers()
    {
        var runtime = new FakeRuntime();
        Assert.IsFalse(OmniSessionCompatibility.RequiresNewSession(new(), runtime));
        var session = new ImageAnalysisLiterarySession
        {
            ModelId = runtime.ModelId, ModelRevision = runtime.ModelRevision,
            HiddenConversation = [new() { Role = "assistant", Content = "Answer" }]
        };
        Assert.IsFalse(OmniSessionCompatibility.RequiresNewSession(session, runtime));
        session.ModelRevision = "older revision";
        Assert.IsTrue(OmniSessionCompatibility.RequiresNewSession(session, runtime));
        session.ModelId = "";
        Assert.IsTrue(OmniSessionCompatibility.RequiresNewSession(session, runtime));
    }

    private sealed class FakeRuntime(string bundle = "light") : IOmniTextRuntime
    {
        public List<List<ImageAnalysisHiddenMessage>> Requests { get; } = [];
        private OmniLlamaProfile Profile => OmniLlamaProfile.ForBundle(bundle)!;
        public string BundleId => bundle;
        public string PipelineId => Profile.PipelineId;
        public string PipelineVersion => Profile.PipelineVersion;
        public string ModelId => Profile.Repository;
        public string ModelRevision => Profile.Revision;
        public string RuntimeId => ImageAnalysisRuntimeIds.Qwen35Llama;
        public string RuntimeVersion => "test";
        public string DeviceMapJson => "{}";
        public bool IsReady => true;
        public ImageAnalysisHeavyResourcePlan CurrentPlan { get; } = new([],0,0,0,0,0,0,0,true,"test");
        public Task<OmniWarmupResult> PrepareAsync(Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken token, bool reuseCurrentPlan = false) => Task.FromResult(new OmniWarmupResult(false, 1, CurrentPlan, "test", "{}", 0,0,0,0,0,0,0));
        public Task<OmniTextGenerationResult> GenerateAsync(string command, string path, IReadOnlyList<ImageAnalysisHiddenMessage> messages, IProgress<ModelStreamChunk>? progress, CancellationToken token, Action<string>? responseReceived = null, Action<string>? diagnosticReceived = null)
        {
            Requests.Add(messages.Select(m => new ImageAnalysisHiddenMessage { Role = m.Role, Content = m.Content, IncludesImage = m.IncludesImage }).ToList());
            var content = command == "analyze" ? "There are two red signs." : "{\"title\":\"Signs\",\"paragraphs\":[\"Two red signs.\"],\"review_items\":[\"Two signs\"],\"uncertainties\":[]}";
            responseReceived?.Invoke(content);
            return Task.FromResult(new OmniTextGenerationResult(content,1,10,10,32768,"eos",0,1,1,10,0,"test","test"));
        }
        public Task<ImageAnalysisHeavyResourceStatus> CaptureResourceStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public void Stop() { }
        public void Dispose() { }
    }
}
