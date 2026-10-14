namespace AIHub.Services;

/// <summary>GPU first; the verified torchaudio 2.10 wheel requires a matching torch 2.10 profile.</summary>
public static class MusicLyricsDevicePolicy
{
    public const long MinimumGpuFreeBytes = 2L * 1024 * 1024 * 1024;
    internal static readonly IReadOnlySet<string> Profiles = new HashSet<string> {
        HardwareRuntimeCatalog.PythonCpuId, HardwareRuntimeCatalog.PythonCudaId,
        HardwareRuntimeCatalog.PythonCuda128Id, HardwareRuntimeCatalog.PythonXpuId
    };
    // The managed ROCm 7.2.1 profile is torch 2.9.1: the pinned 2.10 audio ABI is
    // incompatible. Use CPU until a matching audio dependency is prepared.
    public static bool CanRetryOnCpu(string device, int exitCode, string error, bool cancelled) =>
        !cancelled && device != "cpu" && exitCode == 42 &&
        !error.Contains("DefaultCPUAllocator", StringComparison.OrdinalIgnoreCase) &&
        PythonHardwareFailure.IsRecoverable(error);
}
