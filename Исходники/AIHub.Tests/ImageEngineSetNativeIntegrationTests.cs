using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;
using AIHub.Services;
using Lopata.Inspector;

namespace AIHub.Tests;

/// <summary>Explicit real-model integration, never a default test-suite download or settings change.</summary>
[TestClass, DoNotParallelize]
public sealed class ImageEngineSetNativeIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("001", "z-image", false)]
    [DataRow("002", "z-image", true)]
    [DataRow("002", "krea", false)]
    [DataRow("002", "qwen-image-21-turbo-int8", false)]
    [DataRow("002", "qwen-image-21-base-gguf", false)]
    public async Task ActualWorkerPreservesTheCapturedEngineAndPublishedMetadata(string setId, string modelId, bool pause)
    {
        var modelRoot = Environment.GetEnvironmentVariable("AIHUB_IMAGE_NATIVE_MODEL_ROOT");
        var outputRoot = Environment.GetEnvironmentVariable("AIHUB_ENGINE_NATIVE_OUTPUT");
        if (string.IsNullOrWhiteSpace(modelRoot) || string.IsNullOrWhiteSpace(outputRoot))
            Assert.Inconclusive("Explicit existing model and isolated output directories are required.");
        var inspector = PrepareInspectorStand(setId, modelId, outputRoot);
        var folder = Path.Combine(Path.GetFullPath(outputRoot), "image-" + setId + "-" + modelId + "-" + Guid.NewGuid().ToString("N"));
        var session = ImageGenerationSessionStore.Create(folder);
        var ids = ImageGenerationCatalog.Get(modelId).Components;
        var cards = ImageGenerationCatalog.CreateCards(Path.GetFullPath(modelRoot)).Where(c => ids.Contains(c.ModelArtifactId)
            && c.ModelArtifactId != "generation-runtime").ToArray();
        foreach (var card in cards)
        foreach (var file in card.Files)
        {
            var path = Path.Combine(card.InstallDirectory, file.RelativePath);
            Assert.IsTrue(File.Exists(path), "An existing model artifact is required: " + file.RelativePath);
            Assert.AreEqual(file.SizeBytes, new FileInfo(path).Length);
        }
        if (inspector is not null)
        {
            var library = new ManagedModelLibraryStore();
            foreach (var card in cards) library.Upsert(card);
        }
        var qwen = ImageGenerationCatalog.Get(modelId).Family is not null;
        var side = qwen ? 1024 : 512;
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), modelId,
            "A tabby cat beside a vintage computer monitor and a guitar, quiet blue studio lighting, coherent scene, no text.",
            side, side, pause ? [424242, 424243] : [424242], modelRoot, session)
        {
            EngineSetId = setId, Metadata = new("LOPATA integration test"), OutputFolder = Path.Combine(folder, "export"),
            SubmittedAt = DateTimeOffset.UtcNow, OutputLongestSide = 1280
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var pauseToken = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var resident = inspector is null ? null : new ImageGenerationResident();
        var runtime = resident is null ? new ImageGenerationRuntime()
            : new ImageGenerationRuntime(new ImageGenerationNativeWorker(null, resident));
        byte[]? firstBytes = null;
        if (pause)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.RunAsync(request, cards,
                () => pauseToken.Cancel(), pauseToken.Token));
            var saved = ImageGenerationSessionStore.Load(session).Turns.Single();
            Assert.HasCount(1, saved.Results);
            Assert.AreEqual(setId, saved.Request.EngineSetId);
            firstBytes = File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0));
        }
        var result = inspector is null
            ? await runtime.RunAsync(request, cards, null, timeout.Token)
            : await RunInspectedAsync(inspector, request, cards, runtime, resident!, timeout.Token);
        Assert.HasCount(request.Seeds.Length, result.Results);
        foreach (var image in result.Results)
        {
            Assert.IsNotNull(image.Runtime);
            Assert.AreEqual(setId, image.Runtime.EngineSetId);
            Assert.AreEqual(NativeRuntimeReleasePolicy.SdRevision(setId), image.Runtime.BackendCommit);
            if (qwen)
                Assert.IsTrue(image.Runtime.Placement is "Vram" or "Hybrid");
            Assert.IsTrue(image.Exported);
            Assert.IsTrue(ImageGenerationOutput.IsReady(request, image.Index));
            using var metadata = JsonDocument.Parse(PngTextMetadata.Read(image.ExportPath!)["LOPATA"]);
            Assert.AreEqual(setId, metadata.RootElement.GetProperty("Runtime").GetProperty("EngineSetId").GetString());
            Assert.AreEqual(NativeRuntimeReleasePolicy.SdRevision(setId), metadata.RootElement.GetProperty("BackendCommit").GetString());
            Assert.AreEqual(1280, metadata.RootElement.GetProperty("OutputWidth").GetInt32());
            TestContext.WriteLine(JsonSerializer.Serialize(new { image.Index, image.Runtime, image.ExportPath }));
        }
        if (firstBytes is not null) CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(ImageGenerationSessionStore.ResultPath(request, 0)));
        var times = result.Results.Select(image => File.GetLastWriteTimeUtc(ImageGenerationSessionStore.ResultPath(request, image.Index))).ToArray();
        await runtime.RunAsync(request, cards, null, timeout.Token);
        CollectionAssert.AreEqual(times, result.Results.Select(image => File.GetLastWriteTimeUtc(ImageGenerationSessionStore.ResultPath(request, image.Index))).ToArray());
        Assert.HasCount(request.Seeds.Length, Directory.GetFiles(request.OutputFolder, "*.png"));
    }

    private static InspectorStore? PrepareInspectorStand(string setId, string modelId, string outputRoot)
    {
        if (Environment.GetEnvironmentVariable("AIHUB_INSPECTOR_NATIVE_TEST") != "1") return null;
        if ((setId, modelId) != ("002", "krea")) Assert.Inconclusive("The inspector stand covers Krea in engine set 002.");
        var root = Environment.GetEnvironmentVariable("LOPATA_UPDATE_STAND_ROOT");
        Assert.IsNotNull(root, "An isolated UPDATE_STAND profile is required.");
        Assert.AreEqual(Path.GetFullPath(root), AppDataPaths.BaseDirectory,
            "The test must use the UPDATE_STAND profile.");
        var ordinary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB");
        Assert.IsFalse(string.Equals(AppDataPaths.BaseDirectory, ordinary, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Path.GetFullPath(outputRoot).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Artifacts must remain inside the stand profile.");
        return ApplicationInspector.Initialize();
    }

    private static async Task<ImageGenerationTurn> RunInspectedAsync(InspectorStore inspector,
        ImageGenerationRequest request, IReadOnlyList<ManagedModelArtifactCard> cards,
        ImageGenerationRuntime runtime, ImageGenerationResident resident, CancellationToken token)
    {
        var execution = new BackgroundOperationExecution(new BackgroundOperationStore(
            Path.Combine(request.SessionDirectory, "inspector-operation.json")));
        var input = new BackgroundOperationState
        {
            Id = request.Id,
            Kind = ImageGenerationCatalog.BackgroundKind,
            Title = "Isolated Krea inspector test",
            Input = JsonSerializer.SerializeToElement(request)
        };
        ImageGenerationTurn turn;
        try
        {
            turn = await execution.RunAsync(input, attempt => runtime.RunAsync(request, cards, null, attempt),
                resident.RetireAsync, token);
        }
        finally { await resident.RetireAsync(); }

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var queue = Path.Combine(inspector.DirectoryPath, "queue");
        var events = Directory.GetFiles(queue, "*.json").Order().Select(file =>
            JsonSerializer.Deserialize<InspectorEvent>(File.ReadAllText(file), json)!)
            .Where(item => item.OperationId == request.Id).ToArray();
        var kinds = events.Select(item => item.Kind).ToArray();
        Assert.IsTrue(Array.IndexOf(kinds, InspectorEventKind.Preflight) >= 0);
        Assert.IsTrue(Array.IndexOf(kinds, InspectorEventKind.Preflight) < Array.IndexOf(kinds, InspectorEventKind.AttemptStarted));
        Assert.HasCount(1, events.Where(item => item.Kind == InspectorEventKind.ProcessStarted &&
            item.ProcessId > 0 && item.ProcessStartedUtc is not null));
        var samplingSteps = events.Where(item => item.Kind == InspectorEventKind.Progress && item.Stage == "generating"
                && item.Detail?.StartsWith("step ", StringComparison.Ordinal) == true).Select(item => item.Detail!).ToArray();
        var expectedSteps = ImageGenerationCatalog.Get(request.ModelId).Steps;
        Assert.Contains($"step {expectedSteps}/{expectedSteps}", samplingSteps);
        Assert.IsTrue(samplingSteps.All(step => step.EndsWith($"/{expectedSteps}", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.Progress && item.Stage == "model_loading"));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.Progress && item.Stage == "image_decoding"));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.Saved && item.Outcome == InspectorOutcome.Success));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.AttemptFinished && item.Outcome == InspectorOutcome.Success));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.OperationFinished && item.Outcome == InspectorOutcome.Success));
        Assert.IsTrue(events.Any(item => item.Kind == InspectorEventKind.ProcessExited && item.ProcessId > 0));
        return turn;
    }
}
