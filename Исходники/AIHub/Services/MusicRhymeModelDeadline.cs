using AIHub.Models;

namespace AIHub.Services;

/// <summary>A request-local deadline never changes the shared pause/exit cancellation contract.</summary>
public static class MusicRhymeModelDeadline
{
    public static Task<string> RunAsync(IMusicRhymeModel runtime, DebugModelInfo model, string prompt,
        MusicRhymeTuning tuning, CancellationToken attempt) => RunOperationAsync(
            token => runtime.RewriteAsync(model, prompt, tuning, token), tuning.Integer("GenerationTimeoutSeconds"),
            "Music.Rhyme.Settings.GenerationTimedOut", attempt);

    public static async Task<string> RunOperationAsync(Func<CancellationToken, Task<string>> operation,
        int seconds, string timeoutKey, CancellationToken attempt)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(attempt);
        if (seconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            var result = await operation(deadline.Token);
            // Do not publish late results from a runner that ignored cancellation.
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!attempt.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException(timeoutKey); }
    }
}
