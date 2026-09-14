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
            throw new ImageAnalysisContextExhaustedException("The loaded context has insufficient space for a reply.");
        return checked((int)available);
    }
    public static int FitMargin(long cudaFree, long physicalFree)
    {
        if (cudaFree <= 0 || physicalFree <= 0) throw new IOException("GPU memory inventory is unavailable.");
        var usable = Math.Min(cudaFree, physicalFree);
        var reserve = Math.Max(1024 * MiB, usable / 10);
        return checked((int)((Math.Max(0, cudaFree - physicalFree) + reserve + MiB - 1) / MiB));
    }
    public static async Task<int> FitMarginAsync(CancellationToken ct)
    {
        var physical = await LiteraryGpuBudget.FreeBytesAsync(ct)
            ?? throw new IOException("Physical GPU memory inventory is unavailable.");
        var info = new ProcessStartInfo(LlamaBackendPaths.ServerExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--list-devices");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = OwnedProcessRegistry.Shared.Start(info, "Literary memory inventory");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output + "\n" + await error;
            var match = Regex.Match(text, @"CUDA0\s*:.*\(\d+ MiB, (\d+) MiB free\)");
            if (process.ExitCode != 0 || !match.Success) throw new IOException("CUDA0 memory inventory is unavailable.");
            return FitMargin(long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * MiB, physical);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
