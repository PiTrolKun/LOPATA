using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LetItOutAdaptiveContextTests
{
    private static LiteraryModelMemoryMetadata Metadata(int maximum = 262144) => new("gemma4", 8, maximum, 0, 8L << 30)
    { EmbeddingLength = 2048, HeadCount = 8, KvHeadCount = 2, KeyLength = 256, ValueLength = 256 };

    [TestMethod] public void CpuContextGrowsToTheOfficialAndMetadataLimitsOnlyWhenRamAllows()
    {
        var metadata = Metadata(524288); var kv = LlamaDenseMemoryPolicy.KvBytes(metadata, LetItOutContextPolicy.Minimum);
        var policy = new LetItOutMemoryPolicy(metadata.FileBytes, kv, [1L << 30]);
        var plan = new LetItOutMemoryPlan("Cpu", 0, 0, 2 * metadata.FileBytes + kv);
        Assert.AreEqual(LetItOutContextPolicy.OfficialMaximum, LetItOutContextPolicy.Choose(metadata, policy, plan, null, _ => true));
        metadata = Metadata(32768);
        Assert.AreEqual(32768, LetItOutContextPolicy.Choose(metadata, policy, plan, null, _ => true));
    }
    [TestMethod] public void RamBudgetReducesContextWithoutExceedingTheBudget()
    {
        var metadata = Metadata(); var minimum = LlamaDenseMemoryPolicy.KvBytes(metadata, 8192);
        var policy = new LetItOutMemoryPolicy(metadata.FileBytes, minimum, [1L << 30]);
        var plan = new LetItOutMemoryPlan("Cpu", 0, 0, 2 * metadata.FileBytes + minimum);
        var budget = 2 * metadata.FileBytes + LlamaDenseMemoryPolicy.KvBytes(metadata, 24576);
        Assert.AreEqual(24576, LetItOutContextPolicy.Choose(metadata, policy, plan, null, bytes => bytes <= budget));
        Assert.ThrowsExactly<LetItOutMemoryException>(() => LetItOutContextPolicy.Choose(metadata, policy, plan, null, _ => false));
    }
    [TestMethod] public void GpuWindowPreservesTwoGiBReserveOnAnyCompatibleAdapter()
    {
        var metadata = Metadata(); var minimum = LlamaDenseMemoryPolicy.KvBytes(metadata, 8192);
        var policy = new LetItOutMemoryPolicy(metadata.FileBytes, minimum, [1L << 30]);
        var plan = new LetItOutMemoryPlan("Gpu", 0, metadata.FileBytes + minimum + LetItOutMemoryPolicy.ComputeAllowance, metadata.FileBytes + minimum);
        var free = metadata.FileBytes + LetItOutMemoryPolicy.ComputeAllowance + LetItOutMemoryPolicy.VramReserve + LlamaDenseMemoryPolicy.KvBytes(metadata, 32768);
        var device = new RuntimeDevice("Vulkan1", "Compatible AMD fixture", free + (2L << 30), free);
        var chosen = LetItOutContextPolicy.Choose(metadata, policy, plan, device, _ => true);
        Assert.AreEqual(32768, chosen);
        Assert.IsTrue(plan.GpuBytes - minimum + LlamaDenseMemoryPolicy.KvBytes(metadata, chosen) + LetItOutMemoryPolicy.VramReserve <= device.FreeBytes);
    }

    [TestMethod] public void HybridMovesMoreExpertsToRamToProvideAtLeast64KWithTheSmallestTransfer()
    {
        var metadata = Metadata(); var minimum = LlamaDenseMemoryPolicy.KvBytes(metadata, 8192);
        var policy = new LetItOutMemoryPolicy(metadata.FileBytes, minimum, Enumerable.Repeat(1L << 30, 6).ToArray());
        var device = new RuntimeDevice("Vulkan1", "Compatible adapter", 12L << 30, (10L << 30) + (384L << 20));
        var oldPlan = policy.Choose(device);
        Assert.IsTrue(LetItOutContextPolicy.Choose(metadata, policy, oldPlan, device, _ => true) < 65536);
        var chosen = LetItOutContextPolicy.Place(metadata, policy, device, _ => true);
        Assert.AreEqual("Hybrid", chosen.Plan.Mode); Assert.IsTrue(chosen.Context >= 65536);
        Assert.IsTrue(chosen.Plan.CpuMoeLayers > oldPlan.CpuMoeLayers);
        Assert.IsTrue(chosen.Plan.GpuBytes + LetItOutMemoryPolicy.VramReserve <= device.FreeBytes);
        var preceding = policy.Choose(device, chosen.Plan.CpuMoeLayers - 1);
        Assert.IsTrue(LetItOutContextPolicy.Choose(metadata, policy, preceding, device, _ => true) < 65536);
    }

    [TestMethod] public void HybridKeepsTheLargestFeasibleWindowWhenRamPreventsThePreferredOne()
    {
        var metadata = Metadata(); var minimum = LlamaDenseMemoryPolicy.KvBytes(metadata, 8192);
        var policy = new LetItOutMemoryPolicy(metadata.FileBytes, minimum, Enumerable.Repeat(1L << 30, 6).ToArray());
        var device = new RuntimeDevice("Vulkan0", "Compatible adapter", 12L << 30, (10L << 30) + (896L << 20));
        var budget = metadata.FileBytes + (1L << 30) + LlamaDenseMemoryPolicy.KvBytes(metadata, 24576);
        var chosen = LetItOutContextPolicy.Place(metadata, policy, device, b => b <= budget);
        Assert.AreEqual(24576, chosen.Context); Assert.AreEqual(1, chosen.Plan.CpuMoeLayers);
        Assert.IsTrue(chosen.Plan.HostBytes <= budget);
    }
}
