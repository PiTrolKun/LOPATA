using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutGemmaTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static LetItOutMemoryPolicy Policy => new(12 * GiB, GiB, [2 * GiB, GiB, 3 * GiB, 2 * GiB]);
    private static RuntimeDevice Device(long total, long free) => new("Vulkan1", "Fixture AMD GPU", total, free);

    [TestMethod]
    public void ReserveIsAdditionalToWeightsKvAndBuffersAndExactBoundaryFits()
    {
        var policy = Policy;
        Assert.AreEqual(33 * GiB / 2, policy.FullGpuRequired);
        var full = policy.Choose(Device(24 * GiB, 33 * GiB / 2));
        Assert.AreEqual("Gpu", full.Mode); Assert.AreEqual(29 * GiB / 2, full.GpuBytes);
        var hybrid = policy.Choose(Device(24 * GiB, 33 * GiB / 2 - 1));
        Assert.AreEqual("Hybrid", hybrid.Mode); Assert.AreEqual(1, hybrid.CpuMoeLayers);
        Assert.IsTrue(hybrid.GpuBytes + LetItOutMemoryPolicy.VramReserve <= 33 * GiB / 2 - 1);
    }

    [TestMethod]
    public void OffloadsFirstExpertsByTheirActualUnequalSizesAndFallsBackToCpu()
    {
        var plan = Policy.Choose(Device(12 * GiB, 21 * GiB / 2));
        Assert.AreEqual("Hybrid", plan.Mode); Assert.AreEqual(3, plan.CpuMoeLayers);
        Assert.AreEqual(17 * GiB / 2, plan.GpuBytes);
        Assert.AreEqual(19 * GiB, plan.HostBytes);
        Assert.AreEqual("Cpu", Policy.Choose(Device(8 * GiB, 7 * GiB)).Mode);
        Assert.AreEqual("Cpu", Policy.Choose(null).Mode);
        Assert.AreEqual(25 * GiB, Policy.Choose(null).HostBytes);
    }

    [TestMethod]
    public void OffersClosingAppsOnlyWhenTotalCapacityCanFitIncludingReserve()
    {
        Assert.IsTrue(Policy.ShouldOfferFreeMemory(Device(24 * GiB, 12 * GiB)));
        Assert.IsFalse(Policy.ShouldOfferFreeMemory(Device(12 * GiB, 10 * GiB)));
        Assert.IsFalse(Policy.ShouldOfferFreeMemory(null));
        // Rechecking after an app closes restores the full GPU option, without a saved hybrid preference.
        Assert.AreEqual("Gpu", Policy.Choose(Device(24 * GiB, 20 * GiB)).Mode);
        Assert.IsFalse(Policy.ShouldOfferFreeMemory(Device(24 * GiB, 20 * GiB)));
        // More busy memory can force CPU; a reserve is never dropped to keep GPU enabled.
        Assert.AreEqual("Cpu", Policy.Choose(Device(24 * GiB, 3 * GiB)).Mode);
    }

    [TestMethod]
    public void PinnedModelAndEvaluatedPromptAreKeptSeparateFromGlobalCore()
    {
        var card = ManagedModelCatalog.CreateLetItOut(Path.GetTempPath());
        Assert.AreNotEqual(ManagedModelCatalog.CoreArtifactId, card.ModelArtifactId);
        Assert.AreNotEqual(ManagedModelRoles.Core, card.Role); Assert.IsFalse(card.IsSystem);
        Assert.AreEqual(14_249_047_104, card.Files.Single().SizeBytes);
        StringAssert.Contains(card.Files[0].SourceUrl, ManagedModelCatalog.LetItOutRevision);
        var request = new LetItOutChatRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            LetItOutModel.ModelPath(Path.GetTempPath()), [new("user", "Привет")], "ru");
        using var payload = JsonDocument.Parse(LetItOutChatRuntime.BuildPayload(request));
        var root = payload.RootElement;
        var system = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.AreEqual("e1a9929030dcf0578dcaf8d8b07591aa4cffb96c7c12f9d366a2aab0d39a7349",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(system))).ToLowerInvariant());
        Assert.AreEqual(1d, root.GetProperty("temperature").GetDouble());
        Assert.AreEqual(.95, root.GetProperty("top_p").GetDouble());
        Assert.AreEqual(64, root.GetProperty("top_k").GetInt32());
        Assert.AreEqual(0d, root.GetProperty("min_p").GetDouble());
        Assert.AreEqual(1d, root.GetProperty("repeat_penalty").GetDouble());
        Assert.AreEqual(42, root.GetProperty("seed").GetInt32());
        Assert.AreEqual(1536, root.GetProperty("max_tokens").GetInt32());
        Assert.IsFalse(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [TestMethod]
    public void LibraryAndScenarioFindExistingGemmaInTheSameConfiguredStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "gemma-storage-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        try
        {
            var expected = LetItOutModel.ModelPath(second);
            Directory.CreateDirectory(Path.GetDirectoryName(expected)!); File.WriteAllText(expected, "fixture");
            var settings = new StorageSettings { Models = new() { Locations = [new() { Path = first }, new() { Path = second }] } };
            Assert.AreEqual(second, LetItOutModel.ModelsRoot(settings));
            var card = ManagedModelCatalog.CreatePredefined(settings).Single(c => c.ModelArtifactId == ManagedModelCatalog.LetItOutArtifactId);
            Assert.AreEqual(Path.GetDirectoryName(expected), card.InstallDirectory);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExpertPlacementExcludesAttentionSharedExpertsAndEmbedding()
    {
        var path = Path.Combine(Path.GetTempPath(), "gemma-expert-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                foreach (var (name, offset) in new[] { ("blk.0.ffn_gate_exps.weight", 0ul), ("blk.0.attn_q.weight", 64ul),
                    ("blk.1.ffn_down_exps.weight", 96ul), ("blk.1.ffn_gate_shexp.weight", 224ul), ("token_embd.weight", 256ul) })
                {
                    var bytes = Encoding.UTF8.GetBytes(name); writer.Write((ulong)bytes.Length); writer.Write(bytes);
                    writer.Write(1u); writer.Write(1ul); writer.Write(0u); writer.Write(offset);
                }
                stream.SetLength((stream.Position + 31) / 32 * 32 + 512);
            }
            var metadata = new LiteraryModelMemoryMetadata("gemma4", 2, 32768, 0, new FileInfo(path).Length)
                { TensorDirectoryOffset = 0, TensorCount = 5 };
            var experts = LlamaLayerPlacement.Read(path, metadata, expertsOnly: true);
            CollectionAssert.AreEqual(new long[] { 64, 128 }, experts.LayerBytes.ToArray());
            var all = LlamaLayerPlacement.Read(path, metadata);
            CollectionAssert.AreEqual(new long[] { 96, 160 }, all.LayerBytes.ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
