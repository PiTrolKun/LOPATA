namespace AIHub.Services;

internal static class NativeHardwareFailure
{
    public static bool IsRecoverable(string log) =>
        log.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || log.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
        || log.Contains("VK_ERROR_OUT_OF", StringComparison.OrdinalIgnoreCase)
        || log.Contains("unsupported operation", StringComparison.OrdinalIgnoreCase);
}
