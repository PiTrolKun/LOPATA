namespace AIHub.Services;

internal static class GigaDeviceFallbackPolicy
{
    internal static bool CanRetryOnCpu(string requested, string actual, Exception error, bool cancelled) =>
        !cancelled && requested == "auto" && (actual.StartsWith("cuda:", StringComparison.Ordinal)
            || actual.StartsWith("xpu:", StringComparison.Ordinal))
        && error is LiteraryEmbeddingException && PythonHardwareFailure.IsRecoverable(error.Message);
}
