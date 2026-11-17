using System.Diagnostics;
using AIHub.Models;

namespace AIHub.Services;

public sealed class CoreSpeechPresentationCoordinator(ICoreVoiceEngine engine) : IDisposable
{
    private readonly ICoreVoiceEngine _engine = engine;
    private long _nextPresentationId;

    public bool IsAvailable => _engine.IsAvailable;

    public bool IsRhVoiceAvailable =>
        _engine is CoreVoiceEngineRouter router && router.IsRhVoiceAvailable;

    internal bool IsKittenMiniAvailable =>
        _engine is CoreVoiceEngineRouter router && router.IsKittenMiniAvailable;

    internal string ResolveProvider(string configured) => _engine is CoreVoiceEngineRouter router
        ? router.ResolveProvider(configured)
        : configured;

    public async Task<CoreSpeechPresentationResult> PresentAsync(
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        ISessionEventLog? sessionLog,
        CancellationToken cancellationToken)
    {
        var presentationId = Interlocked.Increment(ref _nextPresentationId);
        var stopwatch = Stopwatch.StartNew();
        var eventPrefix = string.Equals(request.VoiceRole, SpeechRoles.Core, StringComparison.OrdinalIgnoreCase)
            ? "core_voice"
            : "executor_voice";
        sessionLog?.Write(eventPrefix + "_prepare_started", new
        {
            PresentationId = presentationId,
            request.Source,
            request.LanguageCode,
            SegmentCount = request.Segments.Count
        });

        try
        {
            var result = await _engine.SpeakAsync(request, progress, cancellationToken).ConfigureAwait(false);
            sessionLog?.Write(result.Skipped ? eventPrefix + "_skipped" : eventPrefix + "_completed", new
            {
                PresentationId = presentationId,
                request.Source,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                result.UsesNativeWordEvents,
                result.ErrorCode
            });
            return result;
        }
        catch (CoreVoiceException ex)
        {
            sessionLog?.Write(eventPrefix + "_failed", new
            {
                PresentationId = presentationId,
                request.Source,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ex.ErrorCode,
                ErrorType = ex.GetType().FullName
            });
            return new CoreSpeechPresentationResult(false, false, false, ex.ErrorCode);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            sessionLog?.Write(eventPrefix + "_failed", new
            {
                PresentationId = presentationId,
                request.Source,
                DurationMilliseconds = stopwatch.ElapsedMilliseconds,
                ErrorCode = "unexpected_error",
                ErrorType = ex.GetType().FullName
            });
            return new CoreSpeechPresentationResult(false, false, false, "unexpected_error");
        }
    }

    public void Cancel() => _engine.Cancel();

    public void Dispose() => _engine.Dispose();
}
