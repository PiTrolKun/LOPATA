using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Only a known native worker crash is retried; audit and unknown errors escape unchanged.</summary>
internal sealed class VideoH3WorkerCrashException(string detail) : IOException(detail);

internal static class VideoH3Recovery
{
    internal static async Task RunAsync(VideoH3Run run, int limit, Func<Func<double>, Task> attempt,
        Func<Task> commit, Action<string> log, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var before = run.GenerationSeconds;
            var clock = Stopwatch.StartNew();
            double Elapsed() => (before ?? 0) + clock.Elapsed.TotalSeconds;
            try
            {
                await attempt(Elapsed);
                return;
            }
            catch (VideoH3WorkerCrashException) when (!token.IsCancellationRequested && run.RetryAttempts < limit)
            {
                run.RetryAttempts++;
                log("[H3 event] " + JsonSerializer.Serialize(new { kind = "retry", attempt = run.RetryAttempts, total = limit }));
            }
            catch (VideoH3WorkerCrashException error) when (!token.IsCancellationRequested)
            {
                log("[H3 diagnostic] native-crash-retries-exhausted: " + error.Message);
                throw new BackgroundOperationWaitingException("VideoPrep.H3RetriesExhausted");
            }
            finally
            {
                if (before is not null) run.GenerationSeconds = Elapsed();
                await commit();
            }
            // The previous owned process has been reaped before starting another one.
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
