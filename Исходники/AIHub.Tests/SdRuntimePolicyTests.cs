using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class SdRuntimePolicyTests
{
    [TestMethod]
    public void CpuOnlyInventoryDoesNotBecomeVulkanAndOrdinalsAreNotAssumed()
    {
        Assert.IsFalse(SdRuntimeSelector.HasVulkanDevice("CPU\tIntel\n"));
        Assert.IsFalse(SdRuntimeSelector.HasVulkanDevice("Vulkan error\n"));
        Assert.IsFalse(SdRuntimeSelector.HasVulkanDevice("Vulkan0\t\n"));
        Assert.IsTrue(SdRuntimeSelector.HasVulkanDevice("Vulkan3\tAMD Radeon\nCPU\tIntel"));
    }

    [TestMethod]
    public void RetryIsLimitedToHardwareFailures()
    {
        foreach (var error in new[] { "Vulkan out of memory", "failed to allocate device memory", "VK_ERROR_OUT_OF_DEVICE_MEMORY", "unsupported operation" })
            Assert.IsTrue(ImageGenerationNativeWorker.IsHardwareFailure(error));
        foreach (var error in new[] { "invalid prompt", "corrupt model file", "license not confirmed", "canceled" })
            Assert.IsFalse(ImageGenerationNativeWorker.IsHardwareFailure(error));
    }
}
