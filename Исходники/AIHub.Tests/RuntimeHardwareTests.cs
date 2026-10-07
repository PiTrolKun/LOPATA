using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class RuntimeHardwareTests
{
    [TestMethod]
    public void NativeNvidiaDriverMetadataSelectsTheMatchingPythonFamily()
    {
        var expected = Environment.GetEnvironmentVariable("AIHUB_NVIDIA_EXPECT_MAJOR");
        if (string.IsNullOrWhiteSpace(expected)) Assert.Inconclusive("Explicit physical NVIDIA driver check required.");
        var majors = NvidiaDriverCapabilities.ReadComputeMajors();
        Assert.Contains(int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), majors);
        var plan = HardwareRuntimeCatalog.RequiredComponents([new() { Name = "NVIDIA GPU" }], majors);
        Assert.AreEqual(majors.Any(major => major < 10), plan.Contains(HardwareRuntimeCatalog.PythonCudaId));
        Assert.AreEqual(majors.Any(major => major >= 10), plan.Contains(HardwareRuntimeCatalog.PythonCuda128Id));
    }

    [TestMethod]
    [DataRow("AMD Radeon RX 9070 XT", true)]
    [DataRow("AMD Radeon PRO W7900 Dual Slot", true)]
    [DataRow("AMD Radeon RX 7900 XTX", true)]
    [DataRow("AMD Radeon RX 6600", false)]
    [DataRow("AMD Radeon RX 7900 XT", false)]
    [DataRow("AMD Radeon(TM) 890M Graphics", true)]
    [DataRow("AMD Radeon 8060S", true)]
    [DataRow("AMD Radeon Graphics", false)]
    public void RocmAcquisitionHonorsVersionedWindowsMatrix(string name, bool expected)
    {
        var gpu = new GpuPassport { Name = name };
        Assert.AreEqual(expected, HardwareRuntimeCatalog.RequiredComponents([gpu], [], new Version(10, 0, 22631))
            .Contains(HardwareRuntimeCatalog.PythonRocmId));
        Assert.DoesNotContain(HardwareRuntimeCatalog.PythonRocmId,
            HardwareRuntimeCatalog.RequiredComponents([gpu], [], new Version(10, 0, 19045)));
    }

    [TestMethod]
    public void ProbeUsesRuntimeOrdinalsAndFreeMemoryAcrossVendors()
    {
        var devices = RuntimeDeviceProbe.ParseDevices("Available devices:\n  Vulkan0: AMD Radeon (16384 MiB, 1024 MiB free)\n  Vulkan3: Intel Arc (8192 MiB, 7000 MiB free)\n  CUDA2: NVIDIA GPU (24576 MiB, 2000 MiB free)\n");
        Assert.AreEqual(3, devices.Count);
        Assert.AreEqual("Vulkan3", RuntimeDeviceProbe.ChooseGpu(devices, 4L * 1024 * 1024 * 1024)?.Id);
        Assert.IsNull(RuntimeDeviceProbe.ChooseGpu(devices, 8L * 1024 * 1024 * 1024));
    }

    [TestMethod]
    public void InvalidInventoryNeverBecomesAvailableMemory()
    {
        var devices = RuntimeDeviceProbe.ParseDevices("CUDA0: unknown free memory\nVulkan0: Bad (4096 MiB, 8192 MiB free)\nHIP0: Overflow (9223372036854775807 MiB, 1 MiB free)\n");
        Assert.AreEqual(0, devices.Count);
        Assert.IsNull(RuntimeDeviceProbe.ChooseGpu([new("Vulkan0", "GPU", 1024, 0)], 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RuntimeDeviceProbe.ChooseGpu([], -1));
    }

    [TestMethod]
    public void HardwarePlanKeepsCpuAndRequestsVulkanForAnyRealGpu()
    {
        CollectionAssert.AreEqual(new[] { HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.SdCpuId, HardwareRuntimeCatalog.PythonCpuId }, HardwareRuntimeCatalog.RequiredComponents([]).ToArray());
        foreach (var name in new[] { "AMD Radeon", "Intel Arc", "NVIDIA RTX", "Unrecognized hardware vendor" })
        {
            var plan = HardwareRuntimeCatalog.RequiredComponents([new() { Name = name }]);
            foreach (var id in new[] { HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.LlamaVulkanId,
                HardwareRuntimeCatalog.SdCpuId, HardwareRuntimeCatalog.SdVulkanId, HardwareRuntimeCatalog.PythonCpuId })
                Assert.Contains(id, plan);
            Assert.AreEqual(name == "NVIDIA RTX", plan.Contains(HardwareRuntimeCatalog.PythonCudaId));
            Assert.AreEqual(name == "NVIDIA RTX", plan.Contains(HardwareRuntimeCatalog.PythonCuda128Id));
            Assert.AreEqual(name == "Intel Arc", plan.Contains(HardwareRuntimeCatalog.PythonXpuId));
            Assert.AreEqual(name == "NVIDIA RTX" ? 7 : name == "Intel Arc" ? 6 : 5, plan.Count);
        }
        CollectionAssert.AreEqual(new[] { HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.SdCpuId, HardwareRuntimeCatalog.PythonCpuId },
            HardwareRuntimeCatalog.RequiredComponents([new() { Name = "Microsoft Basic Render Driver" }]).ToArray());
    }

    [TestMethod]
    public void DriverCapabilitiesSelectOldNewAndMixedNvidiaLibraries()
    {
        GpuPassport[] inventory = [new() { Name = "NVIDIA compatible hardware" }];
        var older = HardwareRuntimeCatalog.RequiredComponents(inventory, [6, 8]);
        Assert.Contains(HardwareRuntimeCatalog.PythonCudaId, older);
        Assert.DoesNotContain(HardwareRuntimeCatalog.PythonCuda128Id, older);
        var newer = HardwareRuntimeCatalog.RequiredComponents(inventory, [10, 12]);
        Assert.Contains(HardwareRuntimeCatalog.PythonCuda128Id, newer);
        Assert.DoesNotContain(HardwareRuntimeCatalog.PythonCudaId, newer);
        var mixed = HardwareRuntimeCatalog.RequiredComponents(inventory, [6, 12]);
        Assert.Contains(HardwareRuntimeCatalog.PythonCudaId, mixed);
        Assert.Contains(HardwareRuntimeCatalog.PythonCuda128Id, mixed);
        CollectionAssert.AreEquivalent(mixed.ToArray(), HardwareRuntimeCatalog.RequiredComponents(inventory, []).ToArray());
    }

    [TestMethod]
    public void RuntimeArchivesArePinnedAndCpuNeverRequiresCudaAcknowledgement()
    {
        var cpu = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
        var vulkan = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaVulkanId)!;
        Assert.AreEqual(64, cpu.Sha256.Length); Assert.AreEqual(64, vulkan.Sha256.Length);
        Assert.AreEqual("b9442-lopata1", cpu.Version);
        Assert.IsFalse(cpu.LicenseIds.Contains("native.cuda"));
        Assert.IsFalse(vulkan.LicenseIds.Contains("native.cuda"));
        Assert.IsTrue(vulkan.LicenseIds.Contains("runtime.llama-vulkan-loader"));
        Assert.IsFalse(cpu.LicenseIds.Contains("native.libomp"));
        Assert.AreEqual(36_271_681L, cpu.DownloadSizeBytes + vulkan.DownloadSizeBytes);
    }

    [TestMethod]
    public void ExplicitHardwareLicensesDoNotExpandIntoUnrelatedGpuTerms()
    {
        ComponentLicenseEntry[] catalog = [new() { Id = "native.cuda", Basic = true },
            new() { Id = "runtime.llama-engine" }, new() { Id = "runtime.llama-msvc" }];
        var cpu = ComponentCatalog.Find(HardwareRuntimeCatalog.LlamaCpuId)!;
        CollectionAssert.AreEquivalent(cpu.LicenseIds.ToArray(),
            ComponentLicenseService.ExpandSelection(cpu.LicenseIds, catalog).ToArray());
        CollectionAssert.AreEquivalent(new[] { "runtime.llama-engine", "native.cuda" },
            ComponentLicenseService.ExpandSelection(["basic", "runtime.llama-engine", "basic"], catalog).ToArray());
    }
}
