namespace AIHub.Services;

public sealed class MusicGpuException(string message) : Exception(message);

public static class MusicGpuFallback
{
    public static async Task ExecuteAsync(MusicHardwareChoice choice, Func<MusicHardwareChoice, Task> execute,
        Func<Task<MusicHardwareChoice>> cpuChoice, Action<string> log, CancellationToken token)
    {
        try { await execute(choice); }
        catch (MusicGpuException error) when (choice.Device.Backend != "CPU")
        {
            token.ThrowIfCancellationRequested();
            log("[Hardware] GPU initialization/allocation failed; retrying the same stage and seeds on CPU: " + error.Message);
            var cpu = await cpuChoice(); token.ThrowIfCancellationRequested(); await execute(cpu);
        }
    }
}
