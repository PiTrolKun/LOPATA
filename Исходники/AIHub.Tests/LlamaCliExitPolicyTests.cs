using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LlamaCliExitPolicyTests
{
    [TestMethod]
    public void HardwareFailureDiscardsPartialOutputAndOnlyAllowsGpuRetry()
    {
        Assert.ThrowsExactly<NativeGpuExecutionException>(() => LlamaCliExitPolicy.EnsureSuccessful(1, "Vulkan out of memory", true));
        Assert.ThrowsExactly<IOException>(() => LlamaCliExitPolicy.EnsureSuccessful(1, "out of memory", false));
        Assert.ThrowsExactly<IOException>(() => LlamaCliExitPolicy.EnsureSuccessful(1, "invalid GGUF model", true));
        Assert.ThrowsExactly<IOException>(() => LlamaCliExitPolicy.EnsureSuccessful(-1073741819, "", true));
        LlamaCliExitPolicy.EnsureSuccessful(0, "diagnostic: not out of memory", true);
    }
}
