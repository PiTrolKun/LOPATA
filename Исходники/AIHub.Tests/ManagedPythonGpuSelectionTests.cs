using System.IO;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ManagedPythonGpuSelectionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SelectionUsesTheMostFreeCompatibleDeviceAcrossBackendFamilies(bool strict)
    {
        var cuda = Selection(PythonRuntimeProfile.Cuda126, "cuda:1", 10_000);
        var hip = Selection(PythonRuntimeProfile.Rocm721, "cuda:0", 30_000);
        var xpu = Selection(PythonRuntimeProfile.Xpu, "xpu:0", 20_000);
        var choices = new[] { cuda, hip, xpu };
        Assert.AreSame(hip, ManagedPythonRuntime.SelectGpu(choices, [], strict));
        CollectionAssert.AreEqual(new[] { cuda, hip, xpu }, choices,
            "Choosing a device must not mutate the caller's candidate order.");
    }

    [TestMethod]
    public void StrictSelectionDoesNotTurnAFailedNativeGpuProbeIntoCpuFallback()
    {
        var failure = new PythonHardwareProbeException("native probe exited with 0xC0000005");
        var error = Assert.ThrowsExactly<ManagedPythonGpuProbeFailedException>(() =>
            ManagedPythonRuntime.SelectGpu([], [failure], true));
        Assert.IsInstanceOfType<IOException>(error);
    }

    [TestMethod]
    public void LegacySelectionRetainsItsExistingCpuFallbackPolicyAfterProbeFailure()
    {
        Assert.IsNull(ManagedPythonRuntime.SelectGpu([], [new PythonHardwareProbeException("probe failed")], false));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NoCompatibleDeviceAndNoProbeFailureAllowsTheCallerToSelectCpu(bool strict)
    {
        Assert.IsNull(ManagedPythonRuntime.SelectGpu([], [], strict),
            "A supported CPU-only installation is different from a crashed GPU probe.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ASuccessfulCompatibleGpuRemainsUsableWhenAnotherProfileProbeFails(bool strict)
    {
        var gpu = Selection(PythonRuntimeProfile.Xpu, "xpu:1", 24_000);
        Assert.AreSame(gpu, ManagedPythonRuntime.SelectGpu([gpu],
            [new PythonHardwareProbeException("unrelated prepared profile failed")], strict));
    }

    private static ManagedPythonSelection Selection(PythonRuntimeProfile profile, string device, long freeBytes) =>
        new(new ComponentCatalogEntry { Id = "test." + profile }, profile,
            "unused-test-python.exe", device, freeBytes, "deterministic selection fixture");
}
