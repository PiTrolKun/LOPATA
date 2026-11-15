namespace AIHub.Services;

internal sealed class LlamaStartupActivity(TimeSpan inactivityLimit, TimeProvider clock)
{
    private long _lastActivity = clock.GetTimestamp();
    private long _reportedPeakWorkingSet;

    internal bool IsIdle => clock.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= inactivityLimit;

    internal void ObserveLine(string line)
    {
        // Periodic /health requests are not evidence of model loading.
        if (line.Contains("load_tensors:", StringComparison.Ordinal)
            || line.Contains("llama_model_loader:", StringComparison.Ordinal)
            || line.Contains("llama_context:", StringComparison.Ordinal)
            || line.Contains("llama_init_from_model:", StringComparison.Ordinal))
            MarkActive();
    }

    internal void ObservePeakWorkingSet(long bytes)
    {
        // Tensor loading can produce no complete stderr lines until it is done.
        // Count substantial growth so repeated samples and small fluctuations cannot extend the wait.
        const long meaningfulGrowth = 64 * 1024 * 1024;
        if (bytes - _reportedPeakWorkingSet < meaningfulGrowth) return;
        _reportedPeakWorkingSet = bytes;
        MarkActive();
    }

    private void MarkActive() => Interlocked.Exchange(ref _lastActivity, clock.GetTimestamp());
}
