using System.IO;
using System.Net.Http;

namespace AIHub.Services;

/// <summary>Retry only a proven hardware failure, in a fresh CPU process, before any visible output.</summary>
internal static class NativeInferenceRecovery
{
    internal static async Task<T> ExecuteAsync<T>(Func<Task<T>> attempt, Func<bool> usesGpu,
        Func<bool> hardwareFailure, Func<bool> hasVisibleOutput, Func<Task> retire,
        Func<Task> restartCpu, Action<string> log, CancellationToken token)
    {
        try { return await attempt(); }
        catch (Exception) when (token.IsCancellationRequested)
        {
            await retire();
            token.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception error) when (CanRecover(error, usesGpu(), hardwareFailure(), token))
        {
            // Retire even a failed stream with visible output; never keep its faulty worker alive.
            await retire();
            token.ThrowIfCancellationRequested();
            if (hasVisibleOutput()) throw;
            log("GPU inference failed. Retrying the same request once on CPU after process retirement.");
            try
            {
                await restartCpu();
                token.ThrowIfCancellationRequested();
                return await attempt();
            }
            catch
            {
                await retire();
                throw;
            }
        }
    }

    internal static bool CanRecover(Exception error, bool usesGpu, bool hardwareFailure, CancellationToken token) =>
        usesGpu && !token.IsCancellationRequested
        && (error is IOException || error is HttpRequestException http && (http.StatusCode is null || (int)http.StatusCode >= 500))
        && (hardwareFailure || NativeHardwareFailure.IsRecoverable(error.Message));
}
