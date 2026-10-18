using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record SdRuntimeSelection(string ComponentId, string Directory, bool UsesGpu)
{
    public string EngineSetId { get; init; } = NativeRuntimeReleasePolicy.LegacySet;
    public string Backend { get; init; } = "cpu";
    public string Version => NativeRuntimeReleasePolicy.SdVersion(EngineSetId);
    public string Revision => NativeRuntimeReleasePolicy.SdRevision(EngineSetId);
    public string Executable => Path.Combine(Directory, "sd-cli.exe");
}

/// <summary>The pinned executor's auto-fit uses live GGML device memory and chooses its compute GPU.</summary>
internal static class SdRuntimeSelector
{
    public static string? AvailableExecutable(string? engineSetId = null)
    {
        var setId = NativeRuntimeReleasePolicy.ValidateSetId(engineSetId ?? EngineSetSelection.ActiveId);
        var manager = new ComponentManager();
        foreach (var backend in new[] { "vulkan", "cuda12", "cpu" })
        {
            var id = backend == "cpu" ? HardwareRuntimeCatalog.SdCpuId
                : backend == "vulkan" ? HardwareRuntimeCatalog.SdVulkanId : "runtime.sd.cuda12";
            var setDirectory = EngineSetSelection.ResolveDirectory(setId, "sd", backend);
            var directory = setId == NativeRuntimeReleasePolicy.UpdatedSet || backend == "cuda12"
                || HardwareRuntimeBundleVerifier.HasCompleteLayout(id, setDirectory) ? setDirectory
                : manager.GetInstallDirectory(ComponentCatalog.Find(id)!);
            if (setId == NativeRuntimeReleasePolicy.UpdatedSet || backend == "cuda12" ? NativeEngineBundleVerifier.HasCompleteLayout("sd", backend, directory, setId)
                : HardwareRuntimeBundleVerifier.HasCompleteLayout(id, directory)) return Path.Combine(directory, "sd-cli.exe");
        }
        return null;
    }

    public static async Task<SdRuntimeSelection> SelectAsync(IReadOnlyList<ManagedModelArtifactCard> cards,
        bool forceCpu, CancellationToken token, string? engineSetId = null)
    {
        var setId = NativeRuntimeReleasePolicy.ValidateSetId(engineSetId ?? EngineSetSelection.ActiveId);
        var manager = new ComponentManager();
        var backends = forceCpu ? new[] { "cpu" } : ["vulkan", "cuda12", "cpu"];
        foreach (var backend in backends)
        {
            var id = backend == "cpu" ? HardwareRuntimeCatalog.SdCpuId
                : backend == "vulkan" ? HardwareRuntimeCatalog.SdVulkanId : "runtime.sd.cuda12";
            var entry = ComponentCatalog.Find(id);
            var setDirectory = EngineSetSelection.ResolveDirectory(setId, "sd", backend);
            var directory = setId == NativeRuntimeReleasePolicy.UpdatedSet || backend == "cuda12"
                || HardwareRuntimeBundleVerifier.HasCompleteLayout(id, setDirectory) ? setDirectory
                : manager.GetInstallDirectory(entry!);
            if (setId == NativeRuntimeReleasePolicy.UpdatedSet || backend == "cuda12")
            {
                if (!NativeEngineBundleVerifier.HasCompleteLayout("sd", backend, directory, setId)) continue;
                await ComponentLicenseGate.EnsureAsync([setId == NativeRuntimeReleasePolicy.LegacySet ? "generation-runtime" : "runtime.sd-engine-002", "runtime.sd-msvc",
                    .. (backend == "vulkan" ? new[] { "runtime.llama-vulkan-loader" }
                        : backend == "cuda12" && setId == NativeRuntimeReleasePolicy.UpdatedSet ? ["runtime.sd-cuda128-002"] : [])], token);
                await NativeEngineBundleVerifier.VerifyFilesAsync("sd", backend, directory, token, setId);
                var version = await RuntimeDeviceProbe.RunAsync(Path.Combine(directory, "sd-cli.exe"), "--version", token);
                if (!NativeRuntimeReleasePolicy.IsSdVersion(version, setId)) throw new InvalidDataException("Image runtime version differs from the selected engine set.");
            }
            else
            {
                if (!HardwareRuntimeBundleVerifier.HasCompleteLayout(id, directory)) continue;
                await ComponentLicenseGate.EnsureAsync(entry!.LicenseIds, token);
                await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(id, directory, token);
            }
            var executable = Path.Combine(directory, "sd-cli.exe");
            if (backend != "cpu")
            {
                try
                {
                    var inventory = await RuntimeDeviceProbe.RunAsync(executable, "--list-devices", token);
                    if (!HasGpuDevice(inventory, backend == "vulkan" ? "Vulkan" : "CUDA")) continue;
                    return new(id, directory, true) { EngineSetId = setId, Backend = backend };
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                { continue; }
            }
            var weightBytes = cards.Where(card => card.ModelArtifactId != "generation-runtime")
                .Sum(card => card.Files.Sum(file => new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath)).Length));
            var decision = LiteraryRamReservePolicy.EvaluateCurrent(checked(weightBytes * 2));
            if (!decision.Allowed) throw new IOException("Insufficient physical RAM for image generation on CPU.");
            return new(id, directory, false) { EngineSetId = setId, Backend = backend };
        }
        throw new IOException("No verified compatible image generation runtime is available. Prepare hardware libraries first.");
    }

    internal static bool HasVulkanDevice(string inventory) => HasGpuDevice(inventory, "Vulkan");
    internal static bool HasGpuDevice(string inventory, string prefix) => inventory.Split('\n').Any(line =>
        line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && line.Contains('\t') && !string.IsNullOrWhiteSpace(line.Split('\t', 2)[1]));
}
