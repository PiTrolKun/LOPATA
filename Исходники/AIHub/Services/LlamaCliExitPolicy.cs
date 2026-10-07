using System.IO;

namespace AIHub.Services;

internal sealed class NativeGpuExecutionException(string message) : IOException(message);

internal static class LlamaCliExitPolicy
{
    internal static void EnsureSuccessful(int exitCode, string diagnostic, bool usesGpu)
    {
        if (exitCode == 0) return;
        var tail = diagnostic.Length > 8192 ? diagnostic[^8192..] : diagnostic;
        var message = $"llama-cli exited with code {exitCode}: {tail}";
        if (usesGpu && NativeHardwareFailure.IsRecoverable(diagnostic)) throw new NativeGpuExecutionException(message);
        throw new IOException(message);
    }
}
