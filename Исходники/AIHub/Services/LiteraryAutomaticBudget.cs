using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AIHub.Services;

public static class LiteraryAutomaticBudget
{
    public const int SafetyTokens = 256;
    public const int MinimumReply = 256;
    public const long MiB = 1024 * 1024;
    public static int Reply(int context, int input)
    {
        var available = (long)context - input - SafetyTokens;
        if (input < 0 || available < MinimumReply)
            throw new ImageAnalysisContextExhaustedException("The loaded context has insufficient space for a reply.",
                budget: input >= 0 ? new(input, context, SafetyTokens, MinimumReply) : null);
        return checked((int)available);
    }
    public static int FitMargin(long cudaFree, long physicalFree)
    {
        if (cudaFree <= 0 || physicalFree <= 0) throw new IOException("GPU memory inventory is unavailable.");
        var usable = Math.Min(cudaFree, physicalFree);
        var reserve = Math.Max(1024 * MiB, usable / 10);
        return checked((int)((Math.Max(0, cudaFree - physicalFree) + reserve + MiB - 1) / MiB));
    }
    public static async Task<int> FitMarginAsync(CancellationToken ct, LiteraryStartupDiagnostics? diagnostics = null)
    {
        var physical = await LiteraryGpuBudget.FreeBytesAsync(ct, diagnostics)
            ?? throw new IOException("Physical GPU memory inventory is unavailable.");
        var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--list-devices");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        diagnostics?.Record("cuda_inventory_start", new { executable = info.FileName, arguments = info.ArgumentList.ToArray(), physicalFreeBytes = physical });
        using var process = OwnedProcessRegistry.Shared.Start(info, "Literary memory inventory");
        try
        {
            diagnostics?.Record("cuda_inventory_started", new { pid = process.Id });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output; var stderr = await error;
            diagnostics?.Record("cuda_inventory_result", new { pid = process.Id, exitCode = process.ExitCode,
                stdout = LiteraryStartupDiagnostics.Limit(stdout), stderr = LiteraryStartupDiagnostics.Limit(stderr) });
            var text = stdout + "\n" + stderr;
            var match = Regex.Match(text, @"CUDA0\s*:.*\(\d+ MiB, (\d+) MiB free\)");
            if (process.ExitCode != 0 || !match.Success) throw new IOException("CUDA0 memory inventory is unavailable.");
            return FitMargin(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * MiB, physical);
        }
        catch (Exception ex)
        {
            diagnostics?.Record("cuda_inventory_failure", new { exception = ex.ToString(), cancelled = ct.IsCancellationRequested,
                timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested });
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
