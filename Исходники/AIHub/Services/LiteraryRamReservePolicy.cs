using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

public sealed record LiteraryRuntimeOptions(bool UseRamReserve = false, bool RefreshMemory = false);

public sealed class LiteraryRamReserveException(long requiredBytes, long availableBytes)
    : IOException("Insufficient physical RAM for the temporary literary reserve.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}

public sealed class LiteraryGpuContextUnavailableException(bool canUseRamReserve, Exception inner)
    : IOException("The literary model could not allocate CUDA memory.", inner)
{
    public bool CanUseRamReserve { get; } = canUseRamReserve;
}

internal static class LiteraryRamReservePolicy
{
    internal const long GiB = 1024L * 1024 * 1024;
    internal sealed record Decision(long RequiredBytes, long AvailableBytes, long SafetyReserveBytes)
    {
        public bool Allowed => AvailableBytes >= RequiredBytes;
    }

    internal static Decision Evaluate(long modelBytes, long totalBytes, long availableBytes)
    {
        if (modelBytes <= 0 || totalBytes <= 0 || availableBytes < 0 || availableBytes > totalBytes)
            throw new IOException("Physical RAM inventory is unavailable or invalid.");
        // Count the complete mapped model, not just two tensor layers. Leave room for
        // host/repack/compute buffers and Windows without relying on a paging file.
        var reserve = Math.Max(4 * GiB, totalBytes / 10);
        var required = modelBytes > long.MaxValue - 2 * GiB - reserve
            ? long.MaxValue : modelBytes + 2 * GiB + reserve;
        return new(required, availableBytes, reserve);
    }

    internal static Decision EvaluateCurrent(long modelBytes)
    {
        var memory = new MemoryStatus();
        if (!GlobalMemoryStatusEx(memory) || memory.TotalPhysical > long.MaxValue || memory.AvailablePhysical > long.MaxValue)
            throw new IOException("Windows did not provide physical RAM inventory.");
        return Evaluate(modelBytes, (long)memory.TotalPhysical, (long)memory.AvailablePhysical);
    }

    internal static ModelContextBudgetSnapshot Snapshot(LiteraryModelMemoryMetadata? model, int context, int input,
        int minimumReply, bool usesRamReserve)
    {
        var required = (long)input + LiteraryAutomaticBudget.SafetyTokens + minimumReply;
        var canReserve = !usesRamReserve && model?.SupportsRamReserve == true
            && required > context && required <= model.ModelContextTokens;
        return new(input, context, LiteraryAutomaticBudget.SafetyTokens, minimumReply,
            model?.ModelContextTokens, canReserve);
    }

    internal static bool IsExplicitCudaOutOfMemory(string line) =>
        line.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        && (line.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus memory);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatus>();
        public uint MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
