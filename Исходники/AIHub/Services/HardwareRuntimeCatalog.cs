using AIHub.Models;

namespace AIHub.Services;

/// <summary>Pinned hardware libraries acquired through the ordinary component manager.</summary>
public static class HardwareRuntimeCatalog
{
    public const string LlamaCpuId = "runtime.llama.cpu";
    public const string LlamaVulkanId = "runtime.llama.vulkan";
    public const string SdCpuId = "runtime.sd.cpu";
    public const string SdVulkanId = "runtime.sd.vulkan";
    public const string PythonCpuId = "runtime.python.cpu";
    public const string PythonCudaId = "runtime.python.cuda126";
    public const string PythonCuda128Id = "runtime.python.cuda128";
    public const string PythonXpuId = "runtime.python.xpu";
    public const string PythonRocmId = "runtime.python.rocm721";
    internal static PythonRuntimeProfile PythonProfile(string id) => id switch
    {
        PythonCpuId => PythonRuntimeProfile.Cpu,
        PythonCudaId => PythonRuntimeProfile.Cuda126,
        PythonCuda128Id => PythonRuntimeProfile.Cuda128,
        PythonXpuId => PythonRuntimeProfile.Xpu,
        PythonRocmId => PythonRuntimeProfile.Rocm721,
        _ => throw new ArgumentException("Unknown Python hardware component.", nameof(id))
    };
    public static IReadOnlyList<ComponentCatalogEntry> Components { get; } =
    [
        Llama(LlamaCpuId, "CPU", "cpu", 8947853, 33287903,
            "a21940565d63bb05a948572dbcd446e9b89471f994e3d44ae7baa8c10f270d9b",
            ["runtime.llama-engine", "runtime.llama-msvc"]),
        Llama(LlamaVulkanId, "Vulkan", "vulkan", 27323828, 95160871,
            "727c4078a261dc9ecd93a4954ce59e2e911d44d3748c476d14ebf1703403e351",
            ["runtime.llama-engine", "runtime.llama-msvc", "runtime.llama-vulkan-loader"]),
        Sd(SdCpuId, "CPU", "cpu", 22060659, 61617859,
            "8722c549390185e9778784b4e4103cf08a147ced3ccdd10ab936fd926ddaf1f2",
            ["runtime.sd-engine", "runtime.sd-msvc"]),
        Sd(SdVulkanId, "Vulkan", "vulkan", 35838509, 105859920,
            "e731e72c77049952323523803804d41e68559876a1f7cfe9a9d6e5974aaee262",
            ["runtime.sd-engine", "runtime.sd-msvc", "runtime.llama-vulkan-loader"]),
        new()
        {
            Id = PythonCpuId, Name = "Python · PyTorch CPU", Version = "py312-torch210-cpu-transformers530-1",
            Description = "Managed CPU libraries for embeddings, Jelly and SwinIR; model weights are separate.",
            DeliveryKind = ComponentDeliveryKinds.PythonProfile,
            FileName = "python-cpu-downloads", DownloadSizeBytes = 178514752, InstalledSizeBytes = 629083203,
            Sha256 = PythonRuntimeBundleVerifier.CpuManifestDigest,
            Source = "https://pytorch.org/get-started/previous-versions/",
            License = "Original Python and wheel notices; Intel OpenMP terms; Microsoft C++ runtime",
            LicenseIds = ["runtime.python-cpu", "runtime.python-intel-openmp", "runtime.llama-msvc"],
            Dependencies = [LlamaCpuId], HealthCheckRelativePath = "python.exe"
        },
        PythonGpu(PythonCudaId, PythonRuntimeProfile.Cuda126, "CUDA 12.6", 2654724874, 4323315735, "runtime.python-cuda"),
        PythonGpu(PythonCuda128Id, PythonRuntimeProfile.Cuda128, "CUDA 12.8", 2932253048, 4730769399, "runtime.python-cuda128"),
        PythonGpu(PythonXpuId, PythonRuntimeProfile.Xpu, "Intel XPU", 1539313322, 4523721856, "runtime.python-xpu"),
        PythonGpu(PythonRocmId, PythonRuntimeProfile.Rocm721, "AMD ROCm 7.2.1", 2020683409, 4995479633, "runtime.python-rocm721")
    ];

    public static IReadOnlyList<string> RequiredComponents(IEnumerable<GpuPassport> devices, IReadOnlyList<int>? cudaComputeMajors = null,
        Version? windowsVersion = null)
    {
        var inventory = devices.ToArray();
        var ids = new List<string> { LlamaCpuId, SdCpuId, PythonCpuId };
        if (inventory.Any(gpu => !string.IsNullOrWhiteSpace(gpu.Name) && gpu.Name != "unknown"
            && !gpu.Name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)
            && !gpu.Name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)))
            ids.AddRange([LlamaVulkanId, SdVulkanId]);
        if (inventory.Any(gpu => gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            // Actual driver capabilities decide library acquisition, not the developer's GPU.
            // Unknown drivers keep both profiles available; execution still probes real operations.
            if (cudaComputeMajors is null || cudaComputeMajors.Count == 0 || cudaComputeMajors.Any(major => major < 10)) ids.Add(PythonCudaId);
            if (cudaComputeMajors is null || cudaComputeMajors.Count == 0 || cudaComputeMajors.Any(major => major >= 10)) ids.Add(PythonCuda128Id);
        }
        if (inventory.Any(gpu => gpu.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            && (gpu.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) || gpu.Name.Contains("Core Ultra", StringComparison.OrdinalIgnoreCase))))
            ids.Add(PythonXpuId);
        if (inventory.Any(gpu => PythonRocmCompatibility.ShouldAcquire(gpu.Name, windowsVersion ?? Environment.OSVersion.Version)))
            ids.Add(PythonRocmId);
        return ids;
    }

    private static ComponentCatalogEntry PythonGpu(string id, PythonRuntimeProfile profile, string label,
        long downloadBytes, long installedBytes, string license) => new()
    {
        Id = id, Name = "Python · PyTorch " + label,
        Version = "py312-torch" + (profile == PythonRuntimeProfile.Rocm721 ? "291-" : "210-") + profile.Flavor() + "-transformers530-1",
        Description = "Pinned Windows GPU libraries; devices require a working driver and an actual computation probe.",
        DeliveryKind = ComponentDeliveryKinds.PythonProfile, FileName = "python-" + profile.Flavor() + "-downloads",
        DownloadSizeBytes = downloadBytes, InstalledSizeBytes = installedBytes, Sha256 = PythonRuntimeBundleVerifier.ManifestDigest(profile),
        Source = profile == PythonRuntimeProfile.Rocm721
            ? "https://rocm.docs.amd.com/projects/radeon-ryzen/en/docs-7.2.1/docs/install/installrad/windows/install-pytorch.html"
            : "https://pytorch.org/get-started/previous-versions/",
        License = "Original upstream Python/PyTorch/dependency notices and separate vendor terms",
        LicenseIds = ["runtime.python-cpu", "runtime.python-intel-openmp", "runtime.llama-msvc", license],
        Dependencies = [LlamaCpuId], HealthCheckRelativePath = "python.exe"
    };

    private static ComponentCatalogEntry Sd(string id, string label, string flavor,
        long bytes, long installedBytes, string hash, IReadOnlyList<string> licenses)
    {
        var file = $"lopata-sd-3f8527a-lopata1-win-{flavor}-x64.zip";
        return new()
        {
            Id = id, Name = $"stable-diffusion.cpp · {label}", Version = "3f8527a-lopata1",
            Description = $"Managed stable-diffusion.cpp {label} libraries; weights are separate.",
            DeliveryKind = ComponentDeliveryKinds.Archive,
            DownloadUrl = "https://github.com/PiTrolKun/LOPATA/releases/download/v0.4.2-beta/" + file,
            FileName = file, DownloadSizeBytes = bytes, InstalledSizeBytes = installedBytes,
            Sha256 = hash, Source = "https://github.com/leejet/stable-diffusion.cpp/releases/tag/master-929-3f8527a",
            License = "MIT and preserved third-party notices; Microsoft C++ runtime; Vulkan loader when present",
            LicenseIds = licenses, HealthCheckRelativePath = "sd-cli.exe"
        };
    }

    private static ComponentCatalogEntry Llama(string id, string label, string flavor,
        long bytes, long installedBytes, string hash, IReadOnlyList<string> licenses)
    {
        var file = $"lopata-llama-b9442-lopata1-win-{flavor}-x64.zip";
        return new()
        {
            Id = id, Name = $"llama.cpp b9442 · {label}", Version = "b9442-lopata1",
            Description = $"Managed llama.cpp {label} libraries; weights are separate.",
            DeliveryKind = ComponentDeliveryKinds.Archive,
            DownloadUrl = "https://github.com/PiTrolKun/LOPATA/releases/download/v0.4.2-beta/" + file,
            FileName = file, DownloadSizeBytes = bytes, InstalledSizeBytes = installedBytes,
            Sha256 = hash, Source = "https://github.com/ggml-org/llama.cpp/releases/tag/b9442",
            License = "MIT and preserved third-party notices; Microsoft C++ runtime; Vulkan loader when present",
            LicenseIds = licenses, HealthCheckRelativePath = "llama-server.exe"
        };
    }
}
