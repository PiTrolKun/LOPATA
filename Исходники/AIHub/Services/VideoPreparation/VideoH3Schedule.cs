namespace AIHub.Services.VideoPreparation;

/// <summary>A scheduled wait is part of the existing durable background operation.</summary>
internal static class VideoH3Schedule
{
    internal static TimeSpan Remaining(DateTimeOffset due, DateTimeOffset now) =>
        due <= now ? TimeSpan.Zero : due - now;

    internal static async Task WaitAsync(DateTimeOffset due, Action<TimeSpan> waiting,
        CancellationToken token, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = Remaining(due, time.GetUtcNow());
            if (remaining == TimeSpan.Zero) return;
            waiting(remaining);
            // Recheck wall time periodically: sleep/resume or a clock adjustment must not
            // launch before the requested instant. Cancellation uses the normal pause path.
            await Task.Delay(remaining < TimeSpan.FromSeconds(15) ? remaining : TimeSpan.FromSeconds(15), time, token);
        }
    }
}
