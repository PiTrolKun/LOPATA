using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record ManagedPythonSelection(ComponentCatalogEntry Entry, PythonRuntimeProfile Profile,
    string Python, string Device, long FreeBytes, string Reason);

/// <summary>Only the first-launch manager installs libraries. Execution selects verified existing profiles.</summary>
internal static partial class ManagedPythonRuntime
{
    private static readonly string[] GpuProfiles = [HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonCudaId,
        HardwareRuntimeCatalog.PythonXpuId, HardwareRuntimeCatalog.PythonRocmId];

    [GeneratedRegex(@"\A(auto|cpu|cuda(?::[0-9]{1,2})?|hip(?::[0-9]{1,2})?|xpu(?::[0-9]{1,2})?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    internal static bool HasCpu => new ComponentManager().GetStatus()
        .Any(item => item.Entry.Id == HardwareRuntimeCatalog.PythonCpuId && item.IsAvailable);
    internal static string CpuDirectory => new ComponentManager().GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCpuId)!);

    internal static async Task<ManagedPythonSelection> ResolveAsync(string requested, CancellationToken token)
    {
        if (!DevicePattern().IsMatch(requested)) throw new ArgumentException("Unknown Python device policy.", nameof(requested));
        var manager = new ComponentManager();
        var statuses = manager.GetStatus();
        var gpu = new List<ManagedPythonSelection>();
        if (requested != "cpu")
        {
            foreach (var id in GpuProfiles)
            {
                var profile = HardwareRuntimeCatalog.PythonProfile(id);
                var family = profile == PythonRuntimeProfile.Xpu ? "xpu" : profile == PythonRuntimeProfile.Rocm721 ? "hip" : "cuda";
                if (requested != "auto" && !requested.StartsWith(family, StringComparison.Ordinal)) continue;
                var status = statuses.Single(item => item.Entry.Id == id);
                if (!status.IsAvailable) continue;
                await ComponentLicenseGate.EnsureAsync(status.Entry.LicenseIds, token);
                var directory = manager.GetInstallDirectory(status.Entry);
                // Integrity/license failures must not masquerade as a hardware fallback.
                await PythonRuntimeBundleVerifier.VerifyAsync(profile, directory, token);
                try
                {
                    var runtimeRequest = family == "hip" && requested.StartsWith("hip", StringComparison.Ordinal)
                        ? "cuda" + requested[3..] : requested;
                    var probe = await ManagedPythonHardwareProbe.ReadAsync(profile, directory, runtimeRequest, token);
                    if (probe.Device != "cpu") gpu.Add(new(status.Entry, profile, Path.Combine(directory, "python.exe"),
                        probe.Device, probe.FreeBytes, probe.Reason));
                }
                catch (PythonHardwareProbeException error)
                { new ComponentEventLog().Write("python_hardware_probe_failed", new { id, error.Message }); }
            }
        }
        if (gpu.Count > 0) return gpu.OrderByDescending(item => item.FreeBytes).First();
        if (requested is not ("auto" or "cpu")) throw new PythonHardwareProbeException("The requested GPU is unavailable in the prepared Python profiles.");
        var cpu = statuses.Single(item => item.Entry.Id == HardwareRuntimeCatalog.PythonCpuId);
        if (!cpu.IsAvailable) throw new IOException("Prepare Python hardware libraries through the main-window downloader first.");
        await ComponentLicenseGate.EnsureAsync(cpu.Entry.LicenseIds, token);
        var root = manager.GetInstallDirectory(cpu.Entry);
        await PythonRuntimeBundleVerifier.VerifyCpuAsync(root, token);
        var cpuProbe = await ManagedPythonHardwareProbe.ReadAsync(PythonRuntimeProfile.Cpu, root, "cpu", token);
        return new(cpu.Entry, PythonRuntimeProfile.Cpu, Path.Combine(root, "python.exe"), "cpu", cpuProbe.FreeBytes,
            requested == "cpu" ? "Requested CPU" : "No compatible prepared GPU; using CPU");
    }

    internal static async Task<IReadOnlyList<PythonGpuSnapshot>> ListGpuDevicesAsync(CancellationToken token)
    {
        var manager = new ComponentManager(); var statuses = manager.GetStatus();
        var result = new List<PythonGpuSnapshot>();
        foreach (var id in GpuProfiles)
        {
            var status = statuses.Single(item => item.Entry.Id == id);
            if (!status.IsAvailable) continue;
            await ComponentLicenseGate.EnsureAsync(status.Entry.LicenseIds, token);
            var profile = HardwareRuntimeCatalog.PythonProfile(id);
            var root = manager.GetInstallDirectory(status.Entry);
            await PythonRuntimeBundleVerifier.VerifyAsync(profile, root, token);
            try
            {
                var probe = await ManagedPythonHardwareProbe.ReadAsync(profile, root, "auto", token);
                foreach (var device in probe.Devices ?? [])
                    result.Add(profile == PythonRuntimeProfile.Rocm721
                        ? device with { Device = "hip" + device.Device[4..] } : device);
            }
            catch (PythonHardwareProbeException error)
            { new ComponentEventLog().Write("python_device_inventory_failed", new { id, error.Message }); }
        }
        return result.GroupBy(row => (row.Device, row.Name)).Select(group => group.MaxBy(row => row.FreeBytes)!).ToArray();
    }
}
