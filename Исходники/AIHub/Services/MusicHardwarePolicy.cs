namespace AIHub.Services;

public sealed record MusicDevice(string Backend, string Name, string Description, long FreeBytes, long TotalBytes,
    int Capability = 0, int Driver = 0);
public sealed record MusicWeightMemory(long ArBytes, long NarBytes, long VaeBytes, long KvBytesPerToken);
public sealed record MusicMemoryDemand(int ContextTokens, long KvBytes, long GraphReserveBytes, long RequiredBytes);
public sealed record MusicHardwareChoice(MusicDevice Device, MusicMemoryDemand Demand, string Reason);

/// <summary>Pure policy: capability, available memory and stage working sets, never marketing GPU names.</summary>
public static class MusicHardwarePolicy
{
    private const long MiB = 1024 * 1024;
    public static MusicMemoryDemand Demand(MusicWeightMemory weights, int prefixTokens, int abcTokens,
        MusicYueRequest request, bool synthesis)
    {
        request.Validate();
        if (prefixTokens < 0 || abcTokens < 0 || weights.ArBytes <= 0 || weights.NarBytes <= 0 ||
            weights.VaeBytes <= 0 || weights.KvBytesPerToken <= 0) throw new ArgumentOutOfRangeException(nameof(weights));
        var semantic = request.SequenceLimit;
        var branches = synthesis && request.EffectiveExpert.Guidance != 1 ? 2 : 1;
        var needed = checked(prefixTokens + (synthesis ? abcTokens + 2 + semantic * 2 : request.EffectivePlanLimit) + 256);
        // Keep a complete synthesis chunk when possible; upstream itself chunks at the 24,576 ceiling.
        var context = Math.Min(MusicTextBudget.ContextSize, Math.Max(512, checked((needed + 255) / 256 * 256)));
        var kv = checked(weights.KvBytesPerToken * context * branches);
        var workingWeights = synthesis ? Math.Max(weights.ArBytes, checked(weights.NarBytes + weights.VaeBytes)) : weights.ArBytes;
        // Compute/allocator allowance is deliberately an estimate, not an exact allocation guarantee.
        var graph = checked((1024 * MiB + prefixTokens * 32768L + (synthesis ? semantic * 131072L : 0)) * branches);
        var required = checked(workingWeights + kv + graph + 512 * MiB);
        return new(context, kv, graph, required);
    }
    public static bool SupportedCuda(MusicDevice device) => device.Driver >= 12080 && device.Capability is 86 or 89 or 120;
    public static MusicHardwareChoice Choose(IReadOnlyList<MusicDevice> devices, MusicMemoryDemand demand, long availableRam,
        long mappedModelBytes, bool avx2, string? failedGpuReason = null)
    {
        var compatible = devices.Where(d => d.Backend == "Vulkan" || d.Backend == "CUDA" && SupportedCuda(d)).ToArray();
        var gpu = failedGpuReason is null ? compatible.Where(d => d.FreeBytes >= demand.RequiredBytes)
            .OrderBy(d => d.Backend == "CUDA" ? 0 : 1).ThenByDescending(d => d.FreeBytes).FirstOrDefault() : null;
        if (gpu is not null) return new(gpu, demand, "Gpu");
        var reason = failedGpuReason ?? (compatible.Length > 0 ? "GpuMemory" :
            devices.Any(d => d.Backend == "CUDA" && d.Driver < 12080) ? "Driver" : "NoGpu");
        if (!avx2) throw new PlatformNotSupportedException("YuE2 CPU fallback requires AVX2.");
        if (availableRam < checked(demand.RequiredBytes + mappedModelBytes))
            throw new InsufficientMemoryException("Insufficient available memory for GPU and CPU YuE2 execution.");
        return new(new("CPU", "CPU", "CPU AVX2", availableRam, availableRam), demand, reason);
    }
    public static bool IsRecoverableGpuFailure(string diagnostic) => new[] {
        "out of memory", "cudaMalloc", "cuMem", "failed to allocate", "alloc_buffer", "allocation failed",
        "no kernel image", "unsupported PTX", "driver version is insufficient", "failed to initialize",
        "backend not found", "Cannot init backend", "VK_ERROR_DEVICE_LOST", "VK_ERROR_OUT_OF_DEVICE_MEMORY",
        "VK_ERROR_INITIALIZATION_FAILED", "CUDA error" }.Any(s => diagnostic.Contains(s, StringComparison.OrdinalIgnoreCase));
}
