using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.VideoEditor;

internal static class VideoNarratorDevice
{
    // The worker sends bounded packets, including for an hour-long whole narration.
    // Reserve context for one packet and OrpheusCpp's output budget, not the whole document.
    internal static int ContextTokens(string id, string text) => id == "orpheus3b"
        ? 4096 : 0;
    internal static long RequiredBytes(string id, long modelBytes, int context) => modelBytes + 2L * 1024 * 1024 * 1024
        + (id == "orpheus3b" ? context * 114688L : 0);
    // Final preferences are set from the documented per-engine CPU/GPU comparisons.
    internal static bool PreferGpu(string id) => VideoNarratorCatalog.Models.Any(m => m.Id == id);
    internal static HashSet<string> SupportedProfiles(string id)
    {
        var profiles = new HashSet<string>([HardwareRuntimeCatalog.PythonCpuId, HardwareRuntimeCatalog.PythonCudaId,
            HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonXpuId]);
        if (id is "supertonic3" or "vosk010" or "cosyvoice300") profiles.Remove(HardwareRuntimeCatalog.PythonXpuId);
        // These engines do not require a separate torchaudio binary. Other Torch
        // engines currently have pinned matching audio wheels for CUDA/XPU only.
        if (id.StartsWith("kokoro-", StringComparison.Ordinal) || id == "silero56") profiles.Add(HardwareRuntimeCatalog.PythonRocmId);
        return profiles;
    }
    internal static async Task<ManagedPythonSelection> ResolveAsync(string id, string policy, string overlay, string expanded, CancellationToken token)
    {
        var cpu = policy == "cpu" || policy == "auto" && !PreferGpu(id);
        if (id == "orpheus3b")
        {
            var host = await ManagedPythonRuntime.ResolveAsync("cpu", token);
            if (cpu) return host;
            var probe = await ProbeVulkanAsync(host.Python, overlay, expanded, host.Profile.Flavor(), token);
            if (probe.Available) return host with { Device = "vulkan:" + probe.Index, FreeBytes = probe.FreeBytes };
            if (policy == "gpu") throw new IOException("VideoEditor.Voices.GpuUnavailable");
            return host;
        }
        var supported = SupportedProfiles(id);
        // These are two builds for the same CUDA devices. Avoid probing the same GPUs twice per clip.
        if (new ComponentManager().GetStatus().Any(s => s.Entry.Id == HardwareRuntimeCatalog.PythonCuda128Id && s.IsAvailable))
            supported.Remove(HardwareRuntimeCatalog.PythonCudaId);
        var runtime = await ManagedPythonRuntime.ResolveAsync(cpu ? "cpu" : "auto", token, supported);
        if (policy == "gpu" && runtime.Device == "cpu") throw new IOException("VideoEditor.Voices.GpuUnavailable");
        return runtime;
    }
    private sealed record VulkanProbe(bool Available, int Index, long FreeBytes);
    private static async Task<VulkanProbe> ProbeVulkanAsync(string python, string overlay, string expanded, string flavor, CancellationToken token)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        ManagedPythonLaunch.ScriptArguments(start, [Path.Combine(AppContext.BaseDirectory, "Tools", "VideoNarrators", "worker.py"), "--probe-vulkan", overlay, expanded, flavor]);
        using var process = OwnedProcessRegistry.Shared.Start(start, "VideoEditor.NarratorDeviceProbe");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException("Narrator GPU probe failed: " + await error);
            return JsonSerializer.Deserialize<VulkanProbe>(await output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Invalid narrator GPU probe response.");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}

internal static class VideoNarratorBenchmark
{
    private static readonly AsyncLocal<int> Value = new();
    internal static int Repeats => Math.Clamp(Value.Value, 1, 3);
    internal static IDisposable Capture(int repeats) { var before = Value.Value; Value.Value = repeats; return new Scope(before); }
    private sealed class Scope(int before) : IDisposable { public void Dispose() => Value.Value = before; }
}
