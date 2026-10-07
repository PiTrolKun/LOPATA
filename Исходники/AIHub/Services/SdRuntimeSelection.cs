using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record SdRuntimeSelection(string ComponentId, string Directory, bool UsesGpu)
{
    public string Executable => Path.Combine(Directory, "sd-cli.exe");
}

/// <summary>The pinned executor's auto-fit uses live GGML device memory and chooses its compute GPU.</summary>
internal static class SdRuntimeSelector
{
    public static string? AvailableExecutable()
    {
        var manager = new ComponentManager();
        foreach (var id in new[] { HardwareRuntimeCatalog.SdVulkanId, HardwareRuntimeCatalog.SdCpuId })
        {
            var directory = manager.GetInstallDirectory(ComponentCatalog.Find(id)!);
            if (HardwareRuntimeBundleVerifier.HasCompleteLayout(id, directory)) return Path.Combine(directory, "sd-cli.exe");
        }
        return null;
    }

    public static async Task<SdRuntimeSelection> SelectAsync(IReadOnlyList<ManagedModelArtifactCard> cards,
        bool forceCpu, CancellationToken token)
    {
        var manager = new ComponentManager();
        foreach (var id in forceCpu ? new[] { HardwareRuntimeCatalog.SdCpuId }
                     : new[] { HardwareRuntimeCatalog.SdVulkanId, HardwareRuntimeCatalog.SdCpuId })
        {
            var entry = ComponentCatalog.Find(id)!;
            var directory = manager.GetInstallDirectory(entry);
            if (!HardwareRuntimeBundleVerifier.HasCompleteLayout(id, directory)) continue;
            await ComponentLicenseGate.EnsureAsync(entry.LicenseIds, token);
            await HardwareRuntimeBundleVerifier.VerifyExecutableAsync(id, directory, token);
            var executable = Path.Combine(directory, "sd-cli.exe");
            if (id == HardwareRuntimeCatalog.SdVulkanId)
            {
                try
                {
                    var inventory = await RuntimeDeviceProbe.RunAsync(executable, "--list-devices", token);
                    if (!HasVulkanDevice(inventory)) continue;
                    return new(id, directory, true);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
                { continue; }
            }
            var weightBytes = cards.Where(card => card.ModelArtifactId != "generation-runtime")
                .Sum(card => card.Files.Sum(file => new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath)).Length));
            var decision = LiteraryRamReservePolicy.EvaluateCurrent(checked(weightBytes * 2));
            if (!decision.Allowed) throw new IOException("Insufficient physical RAM for image generation on CPU.");
            return new(id, directory, false);
        }
        throw new IOException("No verified compatible image generation runtime is available. Prepare hardware libraries first.");
    }

    internal static bool HasVulkanDevice(string inventory) => inventory.Split('\n').Any(line =>
        line.TrimStart().StartsWith("Vulkan", StringComparison.OrdinalIgnoreCase)
        && line.Contains('\t') && !string.IsNullOrWhiteSpace(line.Split('\t', 2)[1]));
}
