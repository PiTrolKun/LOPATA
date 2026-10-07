using System.IO;

namespace AIHub.Services;

internal static class SemanticVisionMemoryPolicy
{
    internal const int ContextTokens = 4096;

    internal static long GpuRequired(LiteraryModelMemoryMetadata model, long projectorBytes) =>
        checked(LlamaDenseMemoryPolicy.GpuRequired(model, ContextTokens) + VisionBytes(projectorBytes));

    internal static LiteraryRamReservePolicy.Decision CpuDecision(LiteraryModelMemoryMetadata model,
        long projectorBytes, long total, long available) =>
        LiteraryRamReservePolicy.Evaluate(HostBytes(model, projectorBytes), total, available);

    internal static void EnsureCurrentCpuMemory(LiteraryModelMemoryMetadata model, long projectorBytes)
    {
        var decision = LiteraryRamReservePolicy.EvaluateCurrent(HostBytes(model, projectorBytes));
        if (!decision.Allowed)
            throw new IOException($"Insufficient physical RAM for semantic vision: required={decision.RequiredBytes}; available={decision.AvailableBytes}.");
    }

    private static long HostBytes(LiteraryModelMemoryMetadata model, long projectorBytes) =>
        checked(model.FileBytes + LlamaDenseMemoryPolicy.KvBytes(model, ContextTokens) + VisionBytes(projectorBytes));

    private static long VisionBytes(long projectorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectorBytes);
        return checked(projectorBytes + 2 * LlamaDenseMemoryPolicy.GiB);
    }
}
