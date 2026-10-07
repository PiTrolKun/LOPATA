using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public static class MusicHardwareProbe
{
    public static void CleanEnvironment(ProcessStartInfo info)
    {
        // Keep enumeration ordinals identical to the subsequently launched CLI.
        foreach (var key in new[] { "GGML_BACKEND", "GGML_BACKEND_PATH", "GGML_VK_VISIBLE_DEVICES", "CUDA_VISIBLE_DEVICES" })
            info.Environment.Remove(key);
    }
    public static async Task<MusicHardwareChoice> CheckAsync(string directory, string model, string decoder,
        MusicYueRequest request, bool synthesis, CancellationToken token, Action<string>? log = null, string? failedGpuReason = null)
    {
        var weights = JsonSerializer.Deserialize<MusicWeightMemory>(await RunAsync(directory, ["--weights", model, decoder], token))
            ?? throw new InvalidDataException("Missing music memory metadata.");
        var tokenizer = await Task.Run(() => new MusicTokenizer(MusicTokenizerMetadata.Read(model, token)), token);
        var prefix = tokenizer.Count(MusicTextBudget.BuildText(request.Lyrics, request.Style, request.Instruction), token) + 2;
        var abc = synthesis && request.EffectiveExpert.Cot != "off"
            ? string.IsNullOrWhiteSpace(request.Abc) ? request.EffectivePlanLimit : tokenizer.Count(request.Abc, token) : 0;
        var demand = MusicHardwarePolicy.Demand(weights, prefix, abc, request, synthesis);
        var devices = new List<MusicDevice>();
        var probeFailureReason = failedGpuReason;
        if (MusicYueRuntime.IsCuda(directory) && failedGpuReason is null)
            foreach (var backend in new[] { "CUDA", "Vulkan" })
            {
                try
                {
                    using var json = JsonDocument.Parse(await RunAsync(directory, ["--devices", backend], token));
                    var candidates = json.RootElement.GetProperty("Devices").Deserialize<MusicDevice[]>() ?? [];
                    devices.AddRange(candidates);
                    foreach (var device in candidates)
                        log?.Invoke($"[Hardware] candidate={device.Name}; description={device.Description}; backend={device.Backend}; " +
                            $"capability={device.Capability}; driver={device.Driver}; available={device.FreeBytes}; total={device.TotalBytes}");
                }
                catch (Exception error) when (error is InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                    log?.Invoke($"[Hardware] {backend} unavailable: {error.Message}");
                    if (error.Message.Contains("driver version is insufficient", StringComparison.OrdinalIgnoreCase))
                        probeFailureReason = "Driver";
                }
            }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("Cannot query available system memory.");
        var choice = MusicHardwarePolicy.Choose(devices, demand, checked((long)memory.AvailablePhysical),
            checked(new FileInfo(model).Length + new FileInfo(decoder).Length), System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            failedGpuReason ?? (devices.Count == 0 ? probeFailureReason : null));
        log?.Invoke($"[Hardware] device={choice.Device.Name}; backend={choice.Device.Backend}; reason={choice.Reason}; " +
            $"available={choice.Device.FreeBytes}; requiredEstimate={demand.RequiredBytes}; context={demand.ContextTokens}; " +
            $"kv={demand.KvBytes}; graphReserve={demand.GraphReserveBytes}; ar={weights.ArBytes}; nar={weights.NarBytes}; vae={weights.VaeBytes}");
        return choice;
    }
    private static async Task<string> RunAsync(string directory, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, "yue-probe.exe")) { WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        CleanEnvironment(info); foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = OwnedProcessRegistry.Shared.Start(info, "Music.HardwareProbe");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Music hardware probe timed out."); }
            var result = await output; var diagnostic = await errors; token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Hardware probe exit {process.ExitCode}: {diagnostic[..Math.Min(2048, diagnostic.Length)]}");
            if (result.Length > 1_048_576) throw new InvalidDataException("Oversized hardware probe response.");
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(output, errors);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
