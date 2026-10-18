using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

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
    public async Task ActualWorkerPreservesTheCapturedEngineAndPublishedMetadata(string setId, string modelId, bool pause)
    {
        var modelRoot = Environment.GetEnvironmentVariable("AIHUB_IMAGE_NATIVE_MODEL_ROOT");
        var outputRoot = Environment.GetEnvironmentVariable("AIHUB_ENGINE_NATIVE_OUTPUT");
        if (string.IsNullOrWhiteSpace(modelRoot) || string.IsNullOrWhiteSpace(outputRoot))
            Assert.Inconclusive("Explicit existing model and isolated output directories are required.");
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
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), modelId,
            "A tabby cat beside a vintage computer monitor and a guitar, quiet blue studio lighting, coherent scene, no text.",
            512, 512, pause ? [424242, 424243] : [424242], modelRoot, session)
        {
            EngineSetId = setId, Metadata = new("LOPATA integration test"), OutputFolder = Path.Combine(folder, "export"),
            SubmittedAt = DateTimeOffset.UtcNow, OutputLongestSide = 1280
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var pauseToken = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var runtime = new ImageGenerationRuntime();
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
        var result = await runtime.RunAsync(request, cards, null, timeout.Token);
        Assert.HasCount(request.Seeds.Length, result.Results);
        foreach (var image in result.Results)
        {
            Assert.IsNotNull(image.Runtime);
            Assert.AreEqual(setId, image.Runtime.EngineSetId);
            Assert.AreEqual(NativeRuntimeReleasePolicy.SdRevision(setId), image.Runtime.BackendCommit);
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
}
