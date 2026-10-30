using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

internal sealed class VideoVaceWorkerCrashException(string detail) : IOException(detail);

/// <summary>Retries only a reaped native crash; component/audit/validation failures never enter an automatic loop.</summary>
internal static class VideoVaceRecovery
{
    internal static async Task RunAsync(VideoVaceRun run, int limit, Func<Func<double>, Task> attempt,
        Func<Task> commit, Action<string> log, CancellationToken token)
    {
        if (limit is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(limit));
        while (true)
        {
            token.ThrowIfCancellationRequested(); var before = run.GenerationSeconds; var clock = Stopwatch.StartNew();
            double Elapsed() => before + clock.Elapsed.TotalSeconds;
            try { await attempt(Elapsed); return; }
            catch (VideoVaceWorkerCrashException) when (!token.IsCancellationRequested && run.RetryAttempts < limit)
            {
                run.RetryAttempts++;
                log("[VACE event] " + JsonSerializer.Serialize(new { kind = "retry", attempt = run.RetryAttempts, total = limit }));
            }
            catch (VideoVaceWorkerCrashException) when (!token.IsCancellationRequested)
            { throw new BackgroundOperationWaitingException("VideoPrep.VaceRetriesExhausted"); }
            finally { run.GenerationSeconds = Elapsed(); await commit(); }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
