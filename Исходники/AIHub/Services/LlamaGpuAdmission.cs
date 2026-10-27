namespace AIHub.Services;

public enum LlamaGpuDecision { Retry, Hybrid, Cpu, Cancel }
public sealed record LlamaGpuAdmission(string Reason, string Device, long RequiredBytes, long FreeBytes, long TotalBytes);

/// <summary>Video requests prefer full GPU, and require a human choice before avoidable degradation.</summary>
internal static class LlamaGpuAdmissionPolicy
{
    internal static bool FitsHardware(long required, long total) => required > 0 && total > 0 && required <= total - total / 33;
    internal static bool FitsFree(long required, RuntimeDevice device) => FitsHardware(required, device.TotalBytes)
        && required <= Math.Max(0, device.FreeBytes - device.TotalBytes / 33);
}
