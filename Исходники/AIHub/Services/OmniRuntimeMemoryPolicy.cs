using System.IO;

namespace AIHub.Services;

internal static class OmniRuntimeMemoryPolicy
{
    internal static long GpuRequired(LiteraryModelMemoryMetadata model, long projectorBytes) =>
        checked(LlamaDenseMemoryPolicy.GpuRequired(model, OmniLlamaProtocol.ContextTokens)
            + ProjectorAndVisionBuffers(projectorBytes));

    internal static LiteraryRamReservePolicy.Decision CpuDecision(LiteraryModelMemoryMetadata model,
        long projectorBytes, long total, long available) => LiteraryRamReservePolicy.Evaluate(
            HostWorkingBytes(model, projectorBytes), total, available);

    internal static void EnsureCurrentCpuMemory(LiteraryModelMemoryMetadata model, long projectorBytes)
    {
        var decision = LiteraryRamReservePolicy.EvaluateCurrent(HostWorkingBytes(model, projectorBytes));
        if (!decision.Allowed)
            throw new IOException($"Insufficient physical RAM for Omni model, projector and full context: required={decision.RequiredBytes}; available={decision.AvailableBytes}.");
    }

    private static long HostWorkingBytes(LiteraryModelMemoryMetadata model, long projectorBytes) =>
        checked(model.FileBytes + LlamaDenseMemoryPolicy.KvBytes(model, OmniLlamaProtocol.ContextTokens)
            + ProjectorAndVisionBuffers(projectorBytes));

    private static long ProjectorAndVisionBuffers(long projectorBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(projectorBytes);
        // Keep a separate vision encoding buffer allowance in addition to the language graph.
        return checked(projectorBytes + 2 * LlamaDenseMemoryPolicy.GiB);
    }
}
