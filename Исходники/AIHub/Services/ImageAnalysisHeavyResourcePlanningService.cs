using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AIHub.Models;

namespace AIHub.Services;

public sealed class ImageAnalysisHeavyResourcePlanningService
{
    internal const long RequiredGpuBudgetBytes = 14L * 1024 * 1024 * 1024;
    private const long MinimumWindowsReserveBytes = 8L * 1024 * 1024 * 1024;
    private const long MinimumGpuReserveBytes = 1L * 1024 * 1024 * 1024;
    private const double AvailableVramReserveFraction = 0.10;

    public async Task<ImageAnalysisHeavyResourcePlan> MeasureAndPlanAsync(
        CancellationToken cancellationToken)
    {
        var samples = new List<ImageAnalysisHeavyResourceSample>();
        for (var index = 0; index < 5; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(await CaptureSampleAsync(cancellationToken).ConfigureAwait(false));
            if (index < 4)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        return Calculate(samples);
    }

    public Task<ImageAnalysisHeavyResourceSample> CaptureCurrentAsync(
        CancellationToken cancellationToken) => CaptureSampleAsync(cancellationToken);

    public ImageAnalysisHeavyResourcePlan Calculate(
        IReadOnlyList<ImageAnalysisHeavyResourceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one resource sample is required.", nameof(samples));
        }

        var availableRam = samples.Min(item => Math.Max(0, item.AvailableRamBytes));
        var totalRam = samples.Max(item => Math.Max(0, item.TotalRamBytes));
        var commitAvailable = samples.Min(item => Math.Max(0, item.CommitAvailableBytes));
        var availableVramValues = samples
            .Where(item => item.AvailableVramBytes > 0)
            .Select(item => item.AvailableVramBytes)
            .ToList();
        var totalVram = samples.Max(item => Math.Max(0, item.TotalVramBytes));
        var availableVram = availableVramValues.Count == 0 ? 0 : availableVramValues.Min();

        var windowsReserve = Math.Max(MinimumWindowsReserveBytes, (long)(totalRam * 0.10));
        var gpuReserve = totalVram > 0 && availableVram > 0
            ? Math.Max(MinimumGpuReserveBytes, (long)(availableVram * AvailableVramReserveFraction))
            : 0;
        var cpuCeiling = commitAvailable > 0
            ? Math.Min(availableRam, commitAvailable)
            : availableRam;
        var cpuBudget = Math.Max(0, cpuCeiling - windowsReserve);
        var gpuBudget = Math.Max(0, availableVram - gpuReserve);
        return new ImageAnalysisHeavyResourcePlan(
            samples,
            availableRam,
            availableVram,
            commitAvailable,
            cpuBudget,
            gpuBudget,
            windowsReserve,
            gpuReserve,
            gpuBudget >= RequiredGpuBudgetBytes,
            gpuBudget >= RequiredGpuBudgetBytes ? "gpu_only_required" : "gpu_only_unavailable");
    }

    public ImageAnalysisHeavyResourceStatus EvaluatePostWarmupPressure(
        ImageAnalysisHeavyResourcePlan plan,
        ImageAnalysisHeavyResourceSample baseline,
        ImageAnalysisHeavyResourceSample current)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        const long ramDropThreshold = 2L * 1024 * 1024 * 1024;
        const long vramDropThreshold = 1L * 1024 * 1024 * 1024;
        var ramPressure = baseline.AvailableRamBytes - current.AvailableRamBytes >= ramDropThreshold
            && current.AvailableRamBytes < plan.WindowsReserveBytes / 2;
        var commitPressure = baseline.CommitAvailableBytes - current.CommitAvailableBytes >= ramDropThreshold
            && current.CommitAvailableBytes < plan.WindowsReserveBytes / 2;
        var vramPressure = baseline.AvailableVramBytes > 0
            && current.AvailableVramBytes > 0
            && baseline.AvailableVramBytes - current.AvailableVramBytes >= vramDropThreshold
            && current.AvailableVramBytes < plan.GpuReserveBytes / 2;
        return new ImageAnalysisHeavyResourceStatus(
            current,
            ramPressure,
            commitPressure,
            vramPressure,
            ramPressure || commitPressure || vramPressure);
    }

    private static async Task<ImageAnalysisHeavyResourceSample> CaptureSampleAsync(
        CancellationToken cancellationToken)
    {
        var memory = ReadMemory();
        var gpu = await ReadNvidiaMemoryAsync(cancellationToken).ConfigureAwait(false);
        return new ImageAnalysisHeavyResourceSample(
            DateTimeOffset.Now,
            memory.AvailableRamBytes,
            memory.TotalRamBytes,
            memory.CommitAvailableBytes,
            memory.CommitLimitBytes,
            gpu.AvailableBytes,
            gpu.TotalBytes);
    }

    private static (long AvailableRamBytes, long TotalRamBytes, long CommitAvailableBytes, long CommitLimitBytes) ReadMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return (0, 0, 0, 0);
        }
        var totalRam = ToLong(status.TotalPhysical);
        var availableRam = ToLong(status.AvailablePhysical);
        var commitLimit = ToLong(status.TotalPageFile);
        var commitAvailable = ToLong(status.AvailablePageFile);
        return (availableRam, totalRam, commitAvailable, commitLimit);
    }

    private static async Task<(long AvailableBytes, long TotalBytes)> ReadNvidiaMemoryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--query-gpu=memory.free,memory.total");
            startInfo.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = OwnedProcessRegistry.Shared.Start(startInfo, "ImageAnalysisHeavyResourcePlanningService");
            if (process is null)
            {
                return (0, 0);
            }
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return (0, 0);
            }
            var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var values = first?.Split(',', StringSplitOptions.TrimEntries);
            if (values is null || values.Length < 2
                || !long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeMib)
                || !long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalMib))
            {
                return (0, 0);
            }
            return (freeMib * 1024 * 1024, totalMib * 1024 * 1024);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (0, 0);
        }
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

public sealed record ImageAnalysisHeavyResourceSample(
    DateTimeOffset CreatedAt,
    long AvailableRamBytes,
    long TotalRamBytes,
    long CommitAvailableBytes,
    long CommitLimitBytes,
    long AvailableVramBytes,
    long TotalVramBytes);

public sealed record ImageAnalysisHeavyResourcePlan(
    IReadOnlyList<ImageAnalysisHeavyResourceSample> Samples,
    long AvailableRamBytes,
    long AvailableVramBytes,
    long CommitAvailableBytes,
    long CpuBudgetBytes,
    long GpuBudgetBytes,
    long WindowsReserveBytes,
    long GpuReserveBytes,
    bool HasEnoughGpuMemory,
    string Strategy)
{
    public ImageAnalysisPlacementInfo ToPlacementInfo() => new()
    {
        Strategy = Strategy,
        GpuBudgetBytes = GpuBudgetBytes,
        CpuBudgetBytes = CpuBudgetBytes,
        AvailableVramBytes = AvailableVramBytes,
        AvailableRamBytes = AvailableRamBytes,
        CommitAvailableBytes = CommitAvailableBytes,
        CalculatedAt = DateTimeOffset.Now
    };
}

public sealed record ImageAnalysisHeavyResourceStatus(
    ImageAnalysisHeavyResourceSample Sample,
    bool RamPressure,
    bool CommitPressure,
    bool VramPressure,
    bool RestartRecommended);
