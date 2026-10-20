namespace AIHub.Services;

/// <summary>A model-specific verified planner; existing Omni callers retain the ordinary policy.</summary>
internal sealed record OmniRuntimeMemoryOverride(
    Func<LiteraryModelMemoryMetadata, long, long> GpuRequired,
    Action<LiteraryModelMemoryMetadata, long> EnsureCurrentCpuMemory);
