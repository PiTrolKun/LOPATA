using System.IO;
using AIHub.Models;

namespace AIHub.Services;

internal sealed record LetItOutMemoryPlan(string Mode, int CpuMoeLayers, long GpuBytes, long HostBytes);

/// <summary>Budget the exact pinned GGUF expert spans, F16 KV and buffers independently of the user's reserve.</summary>
internal sealed record LetItOutMemoryPolicy(long FileBytes, long KvBytes, IReadOnlyList<long> ExpertLayers)
{
    internal long ExtraHostBytes { get; init; }
    internal const long VramReserve = 2 * LlamaDenseMemoryPolicy.GiB;
    // Lazy compute/graph allocations measured after the first Vulkan generation exceeded
    // the old 1 GiB estimate by ~132 MiB. Keep headroom independent of the 2 GiB user reserve.
    internal const long ComputeAllowance = 3 * LlamaDenseMemoryPolicy.GiB / 2;
    internal long FullGpuBytes => checked(FileBytes + KvBytes + ComputeAllowance);
    internal long FullGpuRequired => checked(FullGpuBytes + VramReserve);

    internal static LetItOutMemoryPolicy Read(string path, LiteraryModelMemoryMetadata metadata,
        int context = LetItOutChatRuntime.ContextTokens)
    {
        if (metadata.Architecture != "gemma4") throw new InvalidDataException("Unexpected scenario model architecture.");
        var experts = LlamaLayerPlacement.Read(path, metadata, expertsOnly: true);
        return new(metadata.FileBytes, LlamaDenseMemoryPolicy.KvBytes(metadata, context), experts.LayerBytes);
    }

    internal bool ShouldOfferFreeMemory(RuntimeDevice? device) => device is not null
        && device.TotalBytes >= FullGpuRequired && device.FreeBytes < FullGpuRequired;

    internal LetItOutMemoryPlan Choose(RuntimeDevice? device, int minimumCpuExperts = 0)
    {
        if (FileBytes <= 0 || KvBytes < 0 || ExpertLayers.Count == 0 || ExpertLayers.Any(b => b <= 0)
            || ExpertLayers.Sum() > FileBytes) throw new InvalidDataException("Invalid Gemma memory budget.");
        if (device is not null && device.TotalBytes > 0 && device.FreeBytes >= 0 && device.FreeBytes <= device.TotalBytes)
        {
            long offloaded = 0;
            // --n-cpu-moe moves the expert tensors in the first N blocks to CPU, retaining attention on GPU.
            for (var count = 0; count <= ExpertLayers.Count; count++)
            {
                var gpu = FullGpuBytes - offloaded;
                if (count >= minimumCpuExperts && gpu + VramReserve <= device.FreeBytes)
                    return new(count == 0 ? "Gpu" : "Hybrid", count, gpu, checked(FileBytes + offloaded + KvBytes + ExtraHostBytes));
                if (count < ExpertLayers.Count) offloaded = checked(offloaded + ExpertLayers[count]);
            }
        }
        // CPU repacking may coexist with the mapped GGUF (observed on the evaluated AVX2 backend).
        return new("Cpu", 0, 0, checked(2 * FileBytes + KvBytes + ExtraHostBytes));
    }

    internal static void EnsureRam(LetItOutMemoryPlan plan)
    {
        if (!LiteraryRamReservePolicy.EvaluateCurrent(plan.HostBytes).Allowed)
            throw new LetItOutMemoryException("LetItOut.RamRequired");
    }
}
