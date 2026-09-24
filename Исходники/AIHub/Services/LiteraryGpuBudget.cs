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
    public static async Task<long?> FreeBytesAsync(CancellationToken token, LiteraryStartupDiagnostics? diagnostics = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var info = new ProcessStartInfo("nvidia-smi.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=memory.free"); info.ArgumentList.Add("--format=csv,noheader,nounits");
            diagnostics?.Record("physical_inventory_start", new { executable = info.FileName, arguments = info.ArgumentList.ToArray() });
            using var process = OwnedProcessRegistry.Shared.Start(info, "Literary GPU inventory");
            try
            {
                diagnostics?.Record("physical_inventory_started", new { pid = process.Id });
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var stdout = await output; var stderr = await error;
                diagnostics?.Record("physical_inventory_result", new { pid = process.Id, exitCode = process.ExitCode,
                    stdout = LiteraryStartupDiagnostics.Limit(stdout), stderr = LiteraryStartupDiagnostics.Limit(stderr) });
                if (process.ExitCode != 0) return null;
                var rows = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var values = new List<long>();
                foreach (var row in rows)
                    if (long.TryParse(row.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib)) values.Add(mib * 1024 * 1024);
                    else return null;
                // With several adapters the minimum never overestimates the selected GPU.
                return values.Count > 0 ? values.Min() : null;
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { diagnostics?.Record("physical_inventory_failure", new { timedOut = true, exception = ex.ToString() }); return null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        { diagnostics?.Record("physical_inventory_failure", new { timedOut = false, exception = ex.ToString() }); return null; }
    }
}
