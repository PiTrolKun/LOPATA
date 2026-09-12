using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AIHub.Services;

public sealed class LiteraryGpuMemoryException(long requiredBytes, long freeBytes) : IOException("Insufficient free GPU memory for the selected extractor.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long FreeBytes { get; } = freeBytes;
}

internal static class LiteraryGpuBudget
{
    // CUDA mem_get_info on WDDM may include reclaimable/shared memory. Prefer the
    // driver's physical free VRAM, and be conservative if inventory is unavailable.
    public static async Task<long?> FreeBytesAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var info = new ProcessStartInfo("nvidia-smi.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=memory.free"); info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = OwnedProcessRegistry.Shared.Start(info, "Literary GPU inventory");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token); await error;
                if (process.ExitCode != 0) return null;
                var rows = (await output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var values = new List<long>();
                foreach (var row in rows)
                    if (long.TryParse(row.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib)) values.Add(mib * 1024 * 1024);
                    else return null;
                // With several adapters the minimum never overestimates the selected GPU.
                return values.Count > 0 ? values.Min() : null;
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
    }
}
