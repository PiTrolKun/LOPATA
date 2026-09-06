using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniGammaTests
{
    [TestMethod]
    [DataRow("light", 0.6, "F16")]
    [DataRow("medium", 0.6, "F16")]
    [DataRow("heavy", 1.0, "BF16")]
    public async Task Profile_DrivesRequestPlacementAndResult(string bundle, double temperature, string projector)
    {
        var profile = OmniLlamaProfile.ForBundle(bundle)!;
        using var request = JsonDocument.Parse(OmniLlamaProtocol.BuildRequest([new() { Role = "user", Content = "Describe" }], "", 4096, profile));
        Assert.AreEqual(temperature, request.RootElement.GetProperty("temperature").GetDouble());
        Assert.AreEqual(0.95, request.RootElement.GetProperty("top_p").GetDouble());
        Assert.IsTrue(request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        using var placement = JsonDocument.Parse(OmniLlamaProtocol.DescribeDeviceMap(profile));
        Assert.AreEqual(projector, placement.RootElement.GetProperty("projectorQuantization").GetString());
        Assert.AreEqual(temperature, placement.RootElement.GetProperty("temperature").GetDouble());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n"));
        var result = await OmniLlamaProtocol.ReadAsync(stream, null, null, default, profile);
        Assert.AreEqual(profile.DiagnosticProfile, result.RuntimeProfile);
        if (bundle == "heavy") Assert.AreEqual("qwen35-ridge-3.7bpw-bf16", result.RuntimeProfile);
    }

    [TestMethod]
    public void Manifest_PinsCompleteRidgePairSeparatelyFromRetiredModel()
    {
        var card = ManagedModelCatalog.CreateOmniGamma(@"C:\models");
        Assert.HasCount(2, card.Files);
        Assert.AreEqual(13_530_332_960L, card.TotalBytes);
        Assert.IsTrue(card.Files.All(f => f.SourceUrl.Contains(ManagedModelCatalog.OmniGammaRepository + "/resolve/" + ManagedModelCatalog.OmniGammaRevision + "/") && f.Sha256.Length == 64));
        Assert.AreEqual("Ridge-3.7bpw", card.Quantization);
        Assert.AreEqual("Apache-2.0", card.License);
        Assert.AreNotEqual(card.InstallDirectory, ManagedModelCatalog.CreateQwen25OmniHeavy(@"C:\models").InstallDirectory);
    }

    [TestMethod]
    public void RetiredGamma_RemainsReadableAndCannotBecomeNewProfileImplicitly()
    {
        var old = new ImageAnalysisLiterarySession { BundleId = "heavy", PipelineId = ImageAnalysisPipelineIds.OmniHeavy,
            ModelId = ManagedModelCatalog.Qwen25OmniRepository, Versions = [new() { Text = "Original text" }] };
        var before = JsonSerializer.Serialize(old);
        Assert.IsTrue(OmniSessionCompatibility.IsRetiredModelSession(old));
        var fresh = new ImageAnalysisLiterarySession();
        OmniLlamaProfile.Gamma.ApplyToNewSession(fresh);
        Assert.IsFalse(OmniSessionCompatibility.IsRetiredModelSession(fresh));
        Assert.AreEqual(ImageAnalysisPipelineIds.OmniGamma, fresh.PipelineId);
        Assert.AreEqual(before, JsonSerializer.Serialize(old));
    }
}
