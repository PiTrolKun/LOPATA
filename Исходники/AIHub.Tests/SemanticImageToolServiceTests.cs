using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class SemanticImageToolServiceTests
{
    [TestMethod]
    public void HardwareRetryRejectsInputErrorsAndCpuFailures()
    {
        var diagnostics = new VisionRuntimeDiagnosticBuffer();
        diagnostics.Add("stderr", "VK_ERROR_OUT_OF_DEVICE_MEMORY");
        Assert.IsTrue(new VisionRuntimeAttemptException(99, System.Net.HttpStatusCode.InternalServerError, "", diagnostics).IsRecoverableHardwareFailure);
        Assert.IsFalse(new VisionRuntimeAttemptException(99, System.Net.HttpStatusCode.BadRequest, "", diagnostics).IsRecoverableHardwareFailure);
        Assert.IsFalse(new VisionRuntimeAttemptException(0, null, "", diagnostics).IsRecoverableHardwareFailure);
        Assert.IsFalse(new VisionRuntimeAttemptException(99, null, "invalid image", new()).IsRecoverableHardwareFailure);
    }

    [TestMethod]
    public void SemanticMemoryIncludesProjectorFullContextAndIndependentVisionBuffers()
    {
        const long gib = 1024L * 1024 * 1024;
        var model = new LiteraryModelMemoryMetadata("llama", 32, 8192, 0, 2 * gib)
            { HeadCount = 32, KvHeadCount = 8, EmbeddingLength = 4096 };
        Assert.AreEqual(LlamaDenseMemoryPolicy.GpuRequired(model, 4096) + 3 * gib,
            SemanticVisionMemoryPolicy.GpuRequired(model, gib));
        Assert.IsFalse(SemanticVisionMemoryPolicy.CpuDecision(model, gib, 16 * gib, 8 * gib).Allowed);
        Assert.IsTrue(SemanticVisionMemoryPolicy.CpuDecision(model, gib, 32 * gib, 24 * gib).Allowed);
        Assert.Throws<ArgumentOutOfRangeException>(() => SemanticVisionMemoryPolicy.GpuRequired(model, 0));
    }

    [TestMethod]
    public void BuildArguments_ConnectsModelAndMultimodalProjector()
    {
        var arguments = SemanticImageToolService.BuildArguments(
            @"C:\models\vision.gguf",
            @"C:\models\projector.gguf",
            54321,
            99,
            "Vulkan3");

        CollectionAssert.Contains(arguments.ToList(), "--mmproj");
        CollectionAssert.Contains(arguments.ToList(), @"C:\models\projector.gguf");
        CollectionAssert.Contains(arguments.ToList(), @"C:\models\vision.gguf");
        CollectionAssert.Contains(arguments.ToList(), "54321");
        CollectionAssert.Contains(arguments.ToList(), "Vulkan3");
        CollectionAssert.Contains(arguments.ToList(), "--mmproj-offload");
        CollectionAssert.Contains(SemanticImageToolService.BuildArguments("model", "projector", 54321, 0, "none").ToList(), "--no-mmproj-offload");
    }

    [TestMethod]
    public void BuildRequestBody_ContainsImageAndGroundedUserInstruction()
    {
        const string dataUri = "data:image/png;base64,AQID";
        const string prompt = "Describe only what is visible.";

        var json = SemanticImageToolService.BuildRequestBody(dataUri, prompt, "ru");
        using var document = JsonDocument.Parse(json);
        var messages = document.RootElement.GetProperty("messages");
        var content = messages[1].GetProperty("content");

        Assert.AreEqual(prompt, content[0].GetProperty("text").GetString());
        Assert.AreEqual(
            dataUri,
            content[1].GetProperty("image_url").GetProperty("url").GetString());
        StringAssert.Contains(messages[0].GetProperty("content").GetString(), "Never invent");
        StringAssert.Contains(messages[0].GetProperty("content").GetString(), "Russian (ru)");
    }

    [TestMethod]
    public async Task VisionImagePayload_WebPIsNormalizedToPngInMemory()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "pixel.webp");
            File.WriteAllBytes(
                path,
                Convert.FromBase64String(
                    "UklGRhwAAABXRUJQVlA4TA8AAAAvAUAAAAcQ/Y/+ByKi/wEA"));
            var payload = await new VisionImagePayloadService().PrepareAsync(
                CreateImageReference(path, ".webp"),
                CancellationToken.None);

            Assert.IsTrue(payload.WasNormalized);
            Assert.AreEqual("image/png", payload.MimeType);
            CollectionAssert.AreEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47 },
                payload.Bytes.Take(4).ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task VisionImagePayload_PngIsPassedThroughWithoutReencoding()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "pixel.png");
            var expected = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9WlS8AAAAASUVORK5CYII=");
            File.WriteAllBytes(path, expected);

            var payload = await new VisionImagePayloadService().PrepareAsync(
                CreateImageReference(path, ".png"),
                CancellationToken.None);

            Assert.IsFalse(payload.WasNormalized);
            Assert.AreEqual("image/png", payload.MimeType);
            CollectionAssert.AreEqual(expected, payload.Bytes);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task VisionImagePayload_LargePngIsDownscaledForLocalRuntimeTransport()
    {
        var root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "wide.png");
            var pixels = new byte[4096 * 32 * 4];
            var bitmap = BitmapSource.Create(
                4096,
                32,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                pixels,
                4096 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            var payload = await new VisionImagePayloadService().PrepareAsync(
                CreateImageReference(path, ".png"),
                CancellationToken.None);

            Assert.IsTrue(payload.WasNormalized);
            using var normalized = new MemoryStream(payload.Bytes);
            var decoded = BitmapDecoder.Create(
                normalized,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            Assert.IsTrue(decoded.PixelWidth <= VisionImagePayloadService.MaximumTransportDimension);
            Assert.IsTrue(decoded.PixelHeight <= VisionImagePayloadService.MaximumTransportDimension);
            Assert.AreEqual(2048, decoded.PixelWidth);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void VisionRuntimeDiagnostics_RedactsImagePayloadAndKeepsHttpFailure()
    {
        var buffer = new VisionRuntimeDiagnosticBuffer();
        buffer.Add("stderr", "decode failed for data:image/webp;base64,AQIDBA==");

        var summary = VisionRuntimeDiagnosticBuffer.CreateAttemptSummary(
            0,
            System.Net.HttpStatusCode.InternalServerError,
            "failed data:image/webp;base64,AQIDBA==",
            buffer.CreateExcerpt(),
            new HttpRequestException("response failed"));

        StringAssert.Contains(summary, "mode=cpu");
        StringAssert.Contains(summary, "http=500");
        StringAssert.Contains(summary, "[redacted]");
        Assert.IsFalse(summary.Contains("AQIDBA", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InferenceFailure_CombinesGpuAndCpuAttemptsIntoSafeToolError()
    {
        var gpuDiagnostics = new VisionRuntimeDiagnosticBuffer();
        gpuDiagnostics.Add("stderr", "gpu decode failed");
        var cpuDiagnostics = new VisionRuntimeDiagnosticBuffer();
        cpuDiagnostics.Add("stderr", "cpu decode failed");

        var failure = SemanticImageToolService.CreateInferenceFailure(
        [
            new VisionRuntimeAttemptException(
                99,
                System.Net.HttpStatusCode.InternalServerError,
                "gpu response",
                gpuDiagnostics),
            new VisionRuntimeAttemptException(
                0,
                System.Net.HttpStatusCode.BadRequest,
                "cpu response",
                cpuDiagnostics)
        ]);

        Assert.AreEqual("semantic_vision_failed", failure.Code);
        StringAssert.Contains(failure.DiagnosticMessage, "mode=gpu");
        StringAssert.Contains(failure.DiagnosticMessage, "mode=cpu");
        StringAssert.Contains(failure.DiagnosticMessage, "http=500");
        StringAssert.Contains(failure.DiagnosticMessage, "http=400");
    }

    private static SessionFileReference CreateImageReference(string path, string extension) =>
        new()
        {
            Id = "image-1",
            SourcePath = path,
            DisplayName = Path.GetFileName(path),
            Extension = extension,
            Category = SessionFileCategories.Image,
            IsAvailable = true,
            SizeBytes = new FileInfo(path).Length
        };

    private static string CreateRoot() =>
        Path.Combine(
            Path.GetTempPath(),
            "AIHubSemanticVisionTests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
