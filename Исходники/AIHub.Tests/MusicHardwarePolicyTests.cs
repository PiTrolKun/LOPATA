using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicHardwarePolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static MusicMemoryDemand Demand => new(4096, GiB / 2, GiB, 5 * GiB);
    [TestMethod]
    [DataRow(86)] [DataRow(89)] [DataRow(120)]
    public void CudaUsesCapabilityAndAvailableMemoryRatherThanName(int sm)
    {
        var gpu = new MusicDevice("CUDA", "CUDA0", "Unrecognized marketing name", 8 * GiB, 24 * GiB, sm, 12080);
        Assert.AreEqual(gpu, MusicHardwarePolicy.Choose([gpu], Demand, 32 * GiB, 4 * GiB, true).Device);
        var occupied = gpu with { FreeBytes = 4 * GiB };
        var cpu = MusicHardwarePolicy.Choose([occupied], Demand, 32 * GiB, 4 * GiB, true);
        Assert.AreEqual("CPU", cpu.Device.Backend); Assert.AreEqual("GpuMemory", cpu.Reason);
    }
    [TestMethod]
    public void AmdAndIntelVulkanRemainCandidatesWithoutNvidiaOrGpuNameAllowlist()
    {
        foreach (var name in new[] { "AMD Radeon", "Intel Arc" })
        {
            var gpu = new MusicDevice("Vulkan", "Vulkan0", name, 8 * GiB, 8 * GiB);
            Assert.AreEqual(gpu, MusicHardwarePolicy.Choose([gpu], Demand, 32 * GiB, 4 * GiB, true).Device);
        }
    }
    [TestMethod]
    public void OldDriverAndNoGpuHaveDifferentCpuReasonsAndCanStillUseVulkan()
    {
        var old = new MusicDevice("CUDA", "CUDA0", "NVIDIA", 8 * GiB, 8 * GiB, 89, 12070);
        Assert.AreEqual("Driver", MusicHardwarePolicy.Choose([old], Demand, 32 * GiB, 4 * GiB, true).Reason);
        Assert.AreEqual("NoGpu", MusicHardwarePolicy.Choose([], Demand, 32 * GiB, 4 * GiB, true).Reason);
        var vk = new MusicDevice("Vulkan", "Vulkan0", "GPU", 8 * GiB, 8 * GiB);
        Assert.AreEqual(vk, MusicHardwarePolicy.Choose([old, vk], Demand, 32 * GiB, 4 * GiB, true).Device);
        Assert.ThrowsExactly<InsufficientMemoryException>(() => MusicHardwarePolicy.Choose([], Demand, 8 * GiB, 4 * GiB, true));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => MusicHardwarePolicy.Choose([], Demand, 32 * GiB, 4 * GiB, false));
    }
    [TestMethod]
    public void StagePeakRetainsKvAndDoesNotSumAllWeightGroups()
    {
        var weights = new MusicWeightMemory(2 * GiB, GiB, GiB / 2, 114688);
        var request = new MusicYueRequest("", "", 123, 456, 30);
        var plan = MusicHardwarePolicy.Demand(weights, 40, 0, request, false);
        var synth = MusicHardwarePolicy.Demand(weights, 40, 300, request, true);
        Assert.AreEqual(4608, plan.ContextTokens); Assert.AreEqual(2304, synth.ContextTokens);
        Assert.AreEqual(synth.ContextTokens * 114688L, synth.KvBytes);
        Assert.AreEqual(2 * GiB + synth.KvBytes + synth.GraphReserveBytes + GiB / 2, synth.RequiredBytes);
        Assert.IsTrue(MusicHardwarePolicy.Demand(weights, 40, 300, request with { DurationSeconds = 360 }, true).RequiredBytes > synth.RequiredBytes);
    }
    [TestMethod]
    public async Task GpuAllocationFailureRetriesOnceAndDoesNotRetryCpuOrCancellationOrInvalidData()
    {
        var gpu = new MusicHardwareChoice(new("CUDA", "CUDA0", "GPU", 8 * GiB, 8 * GiB), Demand, "Gpu");
        var cpu = MusicHardwarePolicy.Choose([], Demand, 32 * GiB, 4 * GiB, true, "GpuFailed");
        var calls = new List<string>();
        await MusicGpuFallback.ExecuteAsync(gpu, selected =>
        { calls.Add(selected.Device.Backend); if (selected.Device.Backend == "CUDA") throw new MusicGpuException("cudaMalloc out of memory"); return Task.CompletedTask; },
            () => Task.FromResult(cpu), _ => { }, default);
        CollectionAssert.AreEqual(new[] { "CUDA", "CPU" }, calls);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MusicGpuFallback.ExecuteAsync(gpu,
            _ => throw new OperationCanceledException(), () => throw new AssertFailedException("No fallback on cancellation"), _ => { }, default));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MusicGpuFallback.ExecuteAsync(gpu,
            _ => throw new InvalidDataException("Bad score"), () => throw new AssertFailedException("No fallback on file errors"), _ => { }, default));
        calls.Clear();
        await Assert.ThrowsExactlyAsync<MusicGpuException>(() => MusicGpuFallback.ExecuteAsync(gpu,
            selected => { calls.Add(selected.Device.Backend); throw new MusicGpuException("Allocation failed"); },
            () => Task.FromResult(cpu), _ => { }, default));
        Assert.AreEqual(2, calls.Count);
    }
    [TestMethod]
    public void OnlyRecognizedNativeGpuErrorsQualifyForRetry()
    {
        foreach (var error in new[] { "cudaMalloc out of memory", "CUDA error: no kernel image is available", "VK_ERROR_DEVICE_LOST", "backend not found" })
            Assert.IsTrue(MusicHardwarePolicy.IsRecoverableGpuFailure(error), error);
        foreach (var error in new[] { "Invalid GGUF", "Cannot open output file", "License declined", "Unexpected score", "Cancelled" })
            Assert.IsFalse(MusicHardwarePolicy.IsRecoverableGpuFailure(error), error);
    }
}
