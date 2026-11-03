using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal static class LetItOutContextPolicy
{
    // Google Gemma 4 model card (03.11.2026): 26B A4B supports 256K.
    // The pinned GGUF's native metadata and the actual backend remain additional limits.
    internal const int OfficialMaximum = 262144;
    internal const int Minimum = 8192;
    internal const int Preferred = 65536;

    internal static (LetItOutMemoryPlan Plan, int Context) Place(LiteraryModelMemoryMetadata metadata,
        LetItOutMemoryPolicy policy, RuntimeDevice? device, Func<long, bool> hostAllowed, int minimumCpuExperts = 0)
    {
        // Keep the fastest placement that provides a useful conversation window. Giving every spare
        // byte to weights first can leave a hybrid with only 8K even when RAM could hold more experts.
        var target = Math.Min(Preferred, Math.Min(metadata.ModelContextTokens, OfficialMaximum));
        LetItOutMemoryPlan? best = null; var bestContext = 0;
        for (var count = Math.Min(minimumCpuExperts, policy.ExpertLayers.Count); count <= policy.ExpertLayers.Count; count++)
        {
            var plan = policy.Choose(device, Math.Max(count, minimumCpuExperts));
            int context;
            try { context = Choose(metadata, policy, plan, device, hostAllowed); }
            catch (LetItOutMemoryException) { continue; }
            var delta = LlamaDenseMemoryPolicy.KvBytes(metadata, context) - policy.KvBytes;
            plan = plan with { GpuBytes = plan.Mode == "Cpu" ? 0 : checked(plan.GpuBytes + delta),
                HostBytes = checked(plan.HostBytes + delta) };
            if (context > bestContext) { best = plan; bestContext = context; }
            if (context >= target || plan.Mode == "Cpu") break;
            // Choose may skip layers that cannot fit even the minimum window.
            count = Math.Max(count, plan.CpuMoeLayers);
        }
        if (best is null) throw new LetItOutMemoryException("LetItOut.RamRequired");
        return (best, bestContext);
    }

    internal static int Choose(LiteraryModelMemoryMetadata metadata, LetItOutMemoryPolicy policy,
        LetItOutMemoryPlan placement, RuntimeDevice? device, Func<long, bool> hostAllowed)
    {
        var maximum = Math.Min(metadata.ModelContextTokens, OfficialMaximum) / 256 * 256;
        if (maximum < Minimum) throw new InvalidDataException("The scenario model has insufficient native context.");
        bool Fits(int context)
        {
            var delta = LlamaDenseMemoryPolicy.KvBytes(metadata, context) - policy.KvBytes;
            var gpu = placement.GpuBytes + delta;
            return (placement.Mode == "Cpu" || device is not null && gpu + LetItOutMemoryPolicy.VramReserve <= device.FreeBytes)
                && hostAllowed(checked(placement.HostBytes + delta));
        }
        if (!Fits(Minimum)) throw new LetItOutMemoryException("LetItOut.RamRequired");
        var low = Minimum / 256; var high = maximum / 256;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Fits(middle * 256)) low = middle; else high = middle - 1;
        }
        return low * 256;
    }
}
