namespace AIHub.Services;

/// <summary>Only a detected loop gets one retry. The caller must dispose/idle the old request first.</summary>
public static class LiteraryLoopRecovery
{
    public static async Task<string> RunAsync(Func<bool, Task<string>> attempt,
        Func<LiteraryLoopEvidence, Task> beforeRetry, CancellationToken token)
    {
        try { return await attempt(false).ConfigureAwait(false); }
        catch (LiteraryLoopException error)
        {
            token.ThrowIfCancellationRequested();
            await beforeRetry(error.Evidence).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        return await attempt(true).ConfigureAwait(false);
    }
}
