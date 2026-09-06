using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class OmniBetaTests
{
    [TestMethod]
    [DataRow("light", "qwen35-q5km-f16", "Q5_K_M")]
    [DataRow("medium", "qwen35-q4km-f16", "Q4_K_M")]
    public async Task Stream_ReportsSelectedProfileWithoutChangingAnswer(string bundle, string expected, string quantization)
    {
        var profile = OmniLlamaProfile.ForBundle(bundle)!;
        const string wire = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var result = await OmniLlamaProtocol.ReadAsync(stream, null, null, default, profile);
        Assert.AreEqual(expected, result.RuntimeProfile);
        Assert.AreEqual("answer", result.Content);
        using var placement = JsonDocument.Parse(OmniLlamaProtocol.DescribeDeviceMap(profile));
        Assert.AreEqual(quantization, placement.RootElement.GetProperty("quantization").GetString());
    }

    [TestMethod]
    public void Manifest_PinsBothOriginsAndLeavesAlphaIndependent()
    {
        var card = ManagedModelCatalog.CreateOmniBeta(@"C:\models");
        Assert.HasCount(2, card.Files);
        Assert.AreEqual(6_698_256_256L, card.TotalBytes);
        StringAssert.Contains(card.Files[0].SourceUrl, "empero-ai/Qwen3.8-9B-Distill-GGUF/resolve/760121cd70bb4c36b2b5ec58eb765e0df5987efe/");
        StringAssert.Contains(card.Files[1].SourceUrl, "unsloth/Qwen3.5-9B-GGUF/resolve/3885219b6810b007914f3a7950a8d1b469d598a5/");
        Assert.IsTrue(card.Files.All(f => f.Sha256.Length == 64));
        Assert.AreNotEqual(card.InstallDirectory, ManagedModelCatalog.CreateOmniAlpha(@"C:\models").InstallDirectory);
        var beta = ImageAnalysisBundleCatalog.Create().Single(b => b.Id == "medium");
        Assert.HasCount(1, beta.Components);
        Assert.AreEqual(32d, beta.Requirements.RamGb);
        Assert.AreEqual(10d, beta.Requirements.VramGb);
    }

    [TestMethod]
    public void LegacyBeta_StaysDistinctFromNewProfile()
    {
        var old = new ImageAnalysisLiterarySession { BundleId = "medium", ModelId = "legacy", Versions = [new() { Text = "Preserved result" }] };
        var snapshot = JsonSerializer.Serialize(old);
        Assert.IsTrue(OmniSessionCompatibility.IsLegacyBeta(old));
        Assert.AreEqual(snapshot, JsonSerializer.Serialize(old));
        var fresh = new ImageAnalysisLiterarySession();
        OmniLlamaProfile.Beta.ApplyToNewSession(fresh);
        Assert.IsFalse(OmniSessionCompatibility.IsLegacyBeta(fresh));
        Assert.AreEqual(ManagedModelCatalog.OmniBetaSessionRevision, fresh.ModelRevision);
    }
}
