using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class GigaDeviceFallbackPolicyTests
{
    [TestMethod]
    [DataRow("cuda:1", "CUDA error: no kernel image is available for execution on the device")]
    [DataRow("cuda:1", "CUDA error: invalid device function")]
    [DataRow("cuda:0", "HIP error: hipErrorNoBinaryForGpu")]
    [DataRow("xpu:1", "SYCL: ZE_RESULT_ERROR_OUT_OF_DEVICE_MEMORY")]
    [DataRow("xpu:0", "SYCL: ZE_RESULT_ERROR_UNSUPPORTED_FEATURE")]
    public void ExplicitBackendCapabilityErrorsAllowAutomaticCpuRetry(string actual, string error)
    {
        Assert.IsTrue(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", actual, new LiteraryEmbeddingException(error), false));
    }

    [TestMethod]
    [DataRow("cuda:2")]
    [DataRow("xpu:1")]
    public void AutomaticGpuHardwareFailureAllowsCpuRetry(string actual)
    {
        Assert.IsTrue(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", actual,
            new LiteraryEmbeddingException("GPU out of memory"), false));
    }

    [TestMethod]
    public void CpuInputExplicitGpuAndCancellationNeverStartAnotherBackend()
    {
        var oom = new LiteraryEmbeddingException("GPU out of memory");
        Assert.IsFalse(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", "cpu", oom, false));
        Assert.IsFalse(GigaDeviceFallbackPolicy.CanRetryOnCpu("cuda", "cuda:1", oom, false));
        Assert.IsFalse(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", "xpu:0", oom, true));
        Assert.IsFalse(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", "cuda:1", new LiteraryEmbeddingException("Invalid input JSON"), false));
        Assert.IsFalse(GigaDeviceFallbackPolicy.CanRetryOnCpu("auto", "cuda:1", new OperationCanceledException(), false));
    }
}
