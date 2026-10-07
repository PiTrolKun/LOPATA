namespace AIHub.Services;

internal static class PythonHardwareFailure
{
    internal static bool IsRecoverable(string message) =>
        !new[] { "FileNotFoundError", "PermissionError", "No space left", "disk is full", "license", "JSONDecodeError" }
            .Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase))
        && (NativeHardwareFailure.IsRecoverable(message)
            || new[] { "no kernel image is available for execution on the device", "CUDA error: invalid device function",
                "hipErrorNoBinaryForGpu", "ZE_RESULT_ERROR_OUT_OF_DEVICE_MEMORY", "ZE_RESULT_ERROR_UNSUPPORTED_FEATURE" }
                .Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase)));
}
