using AIHub.Models;

namespace AIHub.Services;

public static class OmniResponseRecovery
{
    public const int MaximumAttempts = 3;
    public const string WaitingStage = "omni_retry_wait";

    public static async Task<T> RunAsync<T>(Func<int, Task<T>> attempt, string stage,
        Action<string> log, IProgress<ImageAnalysisLiteraryProgress>? progress, CancellationToken token)
    {
        for (var number = 1; ; number++)
        {
            token.ThrowIfCancellationRequested();
            try { return await attempt(number).ConfigureAwait(false); }
            catch (ImageAnalysisOmniFormatException ex)
            {
                token.ThrowIfCancellationRequested();
                log($"Omni format recovery: stage={stage}; attempt={number}/{MaximumAttempts}; error={ex.InnerException?.Message ?? ex.Message}.");
                if (number == MaximumAttempts) throw;
                progress?.Report(new(ManagedModelRoles.Core, WaitingStage, "Waiting for automatic response recovery."));
            }
        }
    }
}
