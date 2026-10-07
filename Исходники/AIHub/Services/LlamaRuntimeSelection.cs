using System.IO;

namespace AIHub.Services;

public sealed record LlamaRuntimeBundle(string Directory, string Backend, IReadOnlyList<string> LicenseIds)
{
    public string? ComponentId { get; init; }
    public string Server => Path.Combine(Directory, "llama-server.exe");
    public string Cli => Path.Combine(Directory, "llama-cli.exe");
}

public sealed record LlamaRuntimeSelection(LlamaRuntimeBundle Bundle, RuntimeDevice? Device)
{
    public bool UsesGpu => Device is not null;
    public string DeviceId => Device?.Id ?? "none";
    public int GpuLayers => UsesGpu ? 99 : 0;
}

/// <summary>Each launch captures its own bundle and ordinal. Running processes are never switched by global state.</summary>
public static class LlamaRuntimeSelector
{
    public static IReadOnlyList<LlamaRuntimeBundle> InstalledBundles()
    {
        var manager = new ComponentManager();
        var result = new List<LlamaRuntimeBundle>();
        foreach (var id in new[] { HardwareRuntimeCatalog.LlamaCpuId, HardwareRuntimeCatalog.LlamaVulkanId })
        {
            var entry = ComponentCatalog.Find(id)!;
            var folder = manager.GetInstallDirectory(entry);
            if (File.Exists(Path.Combine(folder, "llama-server.exe")))
                result.Add(new(folder, id == HardwareRuntimeCatalog.LlamaCpuId ? "CPU" : "Vulkan", entry.LicenseIds) { ComponentId = id });
        }
        return result;
    }

    public static Task<LlamaRuntimeSelection> SelectAsync(long requiredGpuBytes, bool forceCpu, Action<string> log, CancellationToken token) =>
        SelectAsync(InstalledBundles(), requiredGpuBytes, forceCpu, log, token);

    internal static async Task<LlamaRuntimeSelection> SelectAsync(IReadOnlyList<LlamaRuntimeBundle> bundles,
        long requiredGpuBytes, bool forceCpu, Action<string> log, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredGpuBytes);
        var ready = new List<LlamaRuntimeBundle>();
        var candidates = new List<LlamaRuntimeSelection>();
        foreach (var bundle in bundles.Where(bundle => !forceCpu || bundle.Backend == "CPU"))
        {
            token.ThrowIfCancellationRequested();
            await ComponentLicenseGate.EnsureAsync(bundle.LicenseIds, token);
            try
            {
                if (bundle.ComponentId is { } id)
                    await HardwareRuntimeBundleVerifier.VerifyFilesAsync(id, bundle.Directory, token);
                var version = await RuntimeDeviceProbe.RunAsync(bundle.Server, "--version", token);
                if (!HardwareRuntimePreparation.IsPinnedVersion(version)) throw new InvalidDataException("Runtime version differs from the pinned catalog.");
                ready.Add(bundle);
                if (bundle.Backend == "CPU") continue;
                var inventory = await RuntimeDeviceProbe.RunAsync(bundle.Server, "--list-devices", token);
                var device = RuntimeDeviceProbe.ChooseGpu(RuntimeDeviceProbe.ParseDevices(inventory), requiredGpuBytes);
                if (device is not null) candidates.Add(new(bundle, device));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            { log($"Runtime {bundle.Backend} unavailable: {error.Message}"); }
        }
        var selected = candidates.OrderByDescending(item => item.Device!.FreeBytes)
            .ThenBy(item => item.Bundle.Backend == "CUDA" ? 0 : 1).FirstOrDefault();
        selected ??= ready.FirstOrDefault(bundle => bundle.Backend == "CPU") is { } cpu ? new(cpu, null) : null;
        if (selected is null) throw new IOException("No verified compatible llama.cpp runtime is available. Prepare hardware libraries first.");
        log($"Runtime: {selected.Bundle.Backend}; device={selected.DeviceId}; executable={selected.Bundle.Server}; free={selected.Device?.FreeBytes}");
        return selected;
    }
}
