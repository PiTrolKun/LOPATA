using System.Speech.Synthesis;
using AIHub.Models;

namespace AIHub.Services;

public sealed class RhVoiceCoreVoiceEngine : ICoreVoiceEngine
{
    private const string RussianVoiceName = "Aleksandr";
    private const string EnglishVoiceName = "Slt";
    private const string ExecutorRussianVoiceName = "Elena";
    private const string ExecutorEnglishVoiceName = "Bdl";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private SpeechSynthesizer? _activeSynthesizer;
    private bool _disposed;

    public bool IsAvailable => !_disposed && HasVoice(RussianVoiceName) && HasVoice(EnglishVoiceName);

    public async Task<CoreSpeechPresentationResult> SpeakAsync(
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var composition = CoreSpeechTextService.Compose(request.Segments);
        if (string.IsNullOrWhiteSpace(composition.Text))
        {
            progress.Report(CreateProgress(composition, composition.Text.Length, true, false));
            return new CoreSpeechPresentationResult(true, false, false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var synthesizer = new SpeechSynthesizer();
            lock (_sync)
            {
                _activeSynthesizer = synthesizer;
            }

            var isEnglish = request.LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            var useExecutorVoice = string.Equals(
                request.VoiceRole,
                SpeechRoles.UncertaintyExecutor,
                StringComparison.OrdinalIgnoreCase);
            var voiceName = (useExecutorVoice, isEnglish) switch
            {
                (true, true) => SelectAvailableVoice(ExecutorEnglishVoiceName, EnglishVoiceName),
                (true, false) => SelectAvailableVoice(ExecutorRussianVoiceName, RussianVoiceName),
                (false, true) => EnglishVoiceName,
                _ => RussianVoiceName
            };
            SelectVoice(synthesizer, voiceName);
            synthesizer.Volume = Math.Clamp(request.Settings.Volume / 2, 0, 100);
            synthesizer.Rate = Math.Clamp((request.Settings.Rate - 120) / 15, -10, 10);
            synthesizer.SetOutputToDefaultAudioDevice();

            var completion = new TaskCompletionSource<SpeakCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var totalWords = CoreSpeechTextService.CountWords(composition.Text);
            var reportedWords = 0;
            var nativeEvents = 0;
            synthesizer.SpeakProgress += (_, args) =>
            {
                nativeEvents++;
                reportedWords++;
                var visible = totalWords <= 2
                    ? args.CharacterPosition + args.CharacterCount
                    : reportedWords > 1
                        ? args.CharacterPosition
                        : 0;
                if (visible > 0)
                {
                    progress.Report(CreateProgress(composition, visible, false, true));
                }
            };
            synthesizer.SpeakCompleted += (_, args) => completion.TrySetResult(args);

            using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var fallbackTask = RunEstimatedFallbackAsync(
                composition,
                request.Settings.Rate,
                progress,
                () => Volatile.Read(ref nativeEvents) > 0,
                completion.Task,
                fallbackCts.Token);
            using var registration = cancellationToken.Register(Cancel);
            using var notificationSpeech = VoiceNotifications.VoicePlaybackHub.RegisterSpeech(synthesizer.Pause, synthesizer.Resume);
            synthesizer.SpeakAsync(composition.Text);
            var completed = await completion.Task.ConfigureAwait(false);
            fallbackCts.Cancel();
            try
            {
                await fallbackTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (completed.Error is not null)
            {
                throw new CoreVoiceException("rhvoice_playback_failed", "RHVoice playback failed.", completed.Error);
            }

            if (completed.Cancelled || cancellationToken.IsCancellationRequested)
            {
                return new CoreSpeechPresentationResult(false, true, nativeEvents > 0, "cancelled");
            }

            progress.Report(CreateProgress(composition, composition.Text.Length, true, nativeEvents > 0));
            return new CoreSpeechPresentationResult(true, false, nativeEvents > 0);
        }
        catch (OperationCanceledException)
        {
            return new CoreSpeechPresentationResult(false, true, false, "cancelled");
        }
        finally
        {
            lock (_sync)
            {
                _activeSynthesizer = null;
            }

            _gate.Release();
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            try
            {
                _activeSynthesizer?.SpeakAsyncCancelAll();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        _gate.Dispose();
    }

    private static void SelectVoice(SpeechSynthesizer synthesizer, string voiceName)
    {
        var installed = synthesizer.GetInstalledVoices()
            .FirstOrDefault(voice => voice.Enabled
                && string.Equals(voice.VoiceInfo.Name, voiceName, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
        {
            throw new CoreVoiceException(
                "rhvoice_voice_missing",
                $"RHVoice profile '{voiceName}' is not installed.");
        }

        synthesizer.SelectVoice(installed.VoiceInfo.Name);
    }

    private static bool HasVoice(string voiceName)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return synthesizer.GetInstalledVoices()
                .Any(voice => voice.Enabled
                    && string.Equals(voice.VoiceInfo.Name, voiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static string SelectAvailableVoice(string preferredVoice, string fallbackVoice) =>
        HasVoice(preferredVoice) ? preferredVoice : fallbackVoice;

    private static async Task RunEstimatedFallbackAsync(
        CoreSpeechComposition composition,
        int rate,
        IProgress<CoreSpeechProgress> progress,
        Func<bool> hasNativeEvents,
        Task completion,
        CancellationToken cancellationToken)
    {
        await Task.Delay(650, cancellationToken).ConfigureAwait(false);
        if (hasNativeEvents())
        {
            return;
        }

        var timeline = SpeechTimelineBuilder.BuildEstimated(composition.Text, rate);
        var previous = Environment.TickCount64;
        var elapsed = 0L;
        foreach (var cue in timeline)
        {
            if (hasNativeEvents() || completion.IsCompleted)
            {
                return;
            }

            while (cue.TimeMilliseconds > elapsed || VoiceNotifications.VoicePlaybackHub.NotificationsInterrupting)
            {
                if (hasNativeEvents() || completion.IsCompleted) return;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (!VoiceNotifications.VoicePlaybackHub.NotificationsInterrupting) elapsed += now - previous;
                previous = now;
            }

            progress.Report(CreateProgress(composition, cue.VisibleCharacters, false, false));
        }
    }

    private static CoreSpeechProgress CreateProgress(
        CoreSpeechComposition composition,
        int visibleCharacters,
        bool complete,
        bool usesNativeWordEvents) =>
        new(
            CoreSpeechTextService.MapVisibleCharacters(composition, visibleCharacters),
            complete,
            usesNativeWordEvents);
}
