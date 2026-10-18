using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class NativeEngineSetRuntimeTests
{
    [TestMethod]
    public void VersionGateRequiresTheSelectedPublishedBuildAndCommit()
    {
        var legacy = "version: 9442 (d4c8e2c29)\nbuilt with MSVC";
        var current = "version: 0.7.0-dev (build 11669, commit 620d2225c)\nbuilt with MSVC";
        Assert.IsTrue(NativeRuntimeReleasePolicy.IsLlamaVersion(legacy, "001"));
        Assert.IsTrue(NativeRuntimeReleasePolicy.IsLlamaVersion(current, "002"));
        Assert.IsFalse(NativeRuntimeReleasePolicy.IsLlamaVersion(current, "001"));
        Assert.IsFalse(NativeRuntimeReleasePolicy.IsLlamaVersion(legacy, "002"));
        Assert.IsFalse(NativeRuntimeReleasePolicy.IsLlamaVersion(current.Replace("11669", "11700"), "002"));
        Assert.IsFalse(NativeRuntimeReleasePolicy.IsLlamaVersion(current.Replace("620d2225c", "deadbeef0"), "002"));
        Assert.ThrowsExactly<ArgumentException>(() => NativeRuntimeReleasePolicy.LlamaRevision("../../../002"));
        Assert.IsTrue(NativeRuntimeReleasePolicy.IsSdVersion("version: f0aac52", "002"));
        Assert.IsFalse(NativeRuntimeReleasePolicy.IsSdVersion("version: 3f8527a", "002"));
    }

    [TestMethod]
    public void FlashAttentionSyntaxChangesOnlyForTheNewSdContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-sd-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "test", 256, 256, [123], root, root);
            var ids = ImageGenerationCatalog.Get(request.ModelId).Components;
            var cards = ImageGenerationCatalog.CreateCards(root).Where(c => ids.Contains(c.ModelArtifactId)).ToArray();
            var legacy = cards.Single(c => c.ModelArtifactId == "generation-runtime");
            Directory.CreateDirectory(Path.Combine(legacy.InstallDirectory, "bin"));
            File.WriteAllText(Path.Combine(legacy.InstallDirectory, "bin", "sd-cli.exe"), "stub");
            var oldSelection = new SdRuntimeSelection("test", root, true) { EngineSetId = "001", Backend = "vulkan" };
            var newSelection = oldSelection with { EngineSetId = "002" };
            var oldCommand = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "prompt.txt", "old.png", oldSelection);
            var newCommand = ImageGenerationNativeWorker.ManagedCommand(request, 0, cards, "prompt.txt", "new.png", newSelection);
            Assert.IsTrue(oldCommand.ArgumentList.Contains("--diffusion-fa"));
            Assert.IsFalse(oldCommand.ArgumentList.Contains("--flash-attn"));
            Assert.IsFalse(newCommand.ArgumentList.Contains("--diffusion-fa"));
            var args = newCommand.ArgumentList.ToArray();
            Assert.AreEqual("off,diffusion=on", args[Array.IndexOf(args, "--flash-attn") + 1]);
            Assert.IsFalse(args.Contains("--offload-to-cpu"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ImageMetadataUsesExecutedReceiptAndNeverTheRecommendedRuntime()
    {
        var request = new ImageGenerationRequest(Guid.NewGuid().ToString("N"), "z-image", "test", 256, 256, [123], "PRIVATE", "PRIVATE")
            { Metadata = new("author"), EngineSetId = "002" };
        // Simulate a completed old-set result exported after the user's current choice changed.
        var receipt = new ImageGenerationRuntimeReceipt("001", "3f8527a-lopata1", NativeRuntimeReleasePolicy.LegacySdRevision, "cpu", "runtime.sd.cpu");
        using var json = JsonDocument.Parse(ImageGenerationMetadata.Create(request, 0, DateTimeOffset.UtcNow, receipt)["LOPATA"]);
        Assert.AreEqual("001", json.RootElement.GetProperty("Runtime").GetProperty("EngineSetId").GetString());
        Assert.AreEqual(NativeRuntimeReleasePolicy.LegacySdRevision, json.RootElement.GetProperty("BackendCommit").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("Components").EnumerateArray().Any(c => c.GetProperty("Id").GetString() == "generation-runtime"));
        using var unknown = JsonDocument.Parse(ImageGenerationMetadata.Create(request, 0, DateTimeOffset.UtcNow)["LOPATA"]);
        Assert.AreEqual(JsonValueKind.Null, unknown.RootElement.GetProperty("BackendCommit").ValueKind);
        Assert.AreEqual("unknown", unknown.RootElement.GetProperty("RuntimeHistory").GetString());
    }

    [TestMethod]
    public void PlacementCalibrationCannotCrossEngineSetsEvenWithTheSamePath()
    {
        var file = Path.GetTempFileName();
        try
        {
            var bundle = new LlamaRuntimeBundle("same-directory", "CPU", []) { EngineSetId = "001" };
            var oldKey = LlamaPlacementPreferences.Key(file, 32768, new(bundle, null));
            var newKey = LlamaPlacementPreferences.Key(file, 32768, new(bundle with { EngineSetId = "002" }, null));
            Assert.AreNotEqual(oldKey, newKey);
            Assert.AreEqual(oldKey, LlamaPlacementPreferences.Key(file, 32768, new(bundle, null)));
            Assert.AreNotEqual(NativeRuntimeCacheDirectory.ForLlama("same-root", bundle),
                NativeRuntimeCacheDirectory.ForLlama("same-root", bundle with { EngineSetId = "002" }));
            Assert.AreNotEqual(NativeRuntimeCacheDirectory.ForLlama("same-root", bundle),
                NativeRuntimeCacheDirectory.ForLlama("same-root", bundle with { Backend = "Vulkan" }));
        }
        finally { File.Delete(file); }
    }
}
