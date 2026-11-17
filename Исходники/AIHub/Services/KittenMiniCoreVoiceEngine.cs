using System.IO;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using AIHub.Services.VoiceNotifications;
using NAudio.Wave;

namespace AIHub.Services;

internal sealed class KittenMiniCoreVoiceEngine : ICoreVoiceEngine
{
    private readonly ManagedModelLibraryStore _library = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _active;
    private bool _disposed;

    public bool IsAvailable => !_disposed
        && IsInstalled(VideoNarratorCatalog.ArtifactId(VideoNarratorCatalog.KittenMini))
        && IsInstalled("video-narrator-runtime-" + VideoNarratorCatalog.KittenMini)
        && ManagedPythonRuntime.HasCpu;

    private bool IsInstalled(string artifactId) => _library.Load(artifactId) is
        { Status: ManagedModelStatuses.Installed } card && Directory.Exists(card.InstallDirectory);

    public async Task<CoreSpeechPresentationResult> SpeakAsync(
        CoreSpeechRequest request, IProgress<CoreSpeechProgress> progress, CancellationToken cancellationToken)
    {
        var composition = CoreSpeechTextService.Compose(request.Segments);
        if (composition.Text.Length == 0)
        {
            Report(composition, 0, true, progress);
            return new(true, false, false);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            _active = cancel;
        }

        DirectoryInfo? workspace = null;
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            workspace = Directory.CreateTempSubdirectory("lopata-core-voice-");
            using var scope = VideoNarrationWorkspace.Capture(workspace.FullName, publishOutput: false);
            var id = VideoNarratorCatalog.KittenMini;
            var language = request.LanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
            var rate = Math.Clamp((int)Math.Round(request.Settings.Rate * 100d / 120d), 50, 200);
            var volume = Math.Clamp(request.Settings.Volume / 200f, 0, 1);
            using var narrator = new VideoNarratorRegistry(_library).Create(id);
            var audio = await narrator.GenerateAsync(id, language, composition.Text, rate, cancel.Token).ConfigureAwait(false);
            await PlayAsync(audio.Path, composition, request.Settings.Rate, volume, progress, cancel.Token).ConfigureAwait(false);
            Report(composition, composition.Text.Length, true, progress);
            return new(true, false, false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new(false, true, false, "cancelled");
        }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(_active, cancel)) _active = null;
            if (workspace is not null)
                try { workspace.Delete(recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { OwnedProcessRegistry.Log("core_voice_cleanup_failed", "CoreVoice.KittenMini", detail: error.GetType().Name); }
        }
    }

    private static async Task PlayAsync(string path, CoreSpeechComposition composition, int rate, float volume,
        IProgress<CoreSpeechProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new WaveFileReader(path);
        using var player = new WaveOutEvent { DesiredLatency = 100 };
        player.Init(reader);
        player.Volume = volume;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is { } error) ended.TrySetException(error);
            else ended.TrySetResult();
        };

        using var speech = VoicePlaybackHub.RegisterSpeech(player.Pause, player.Play);
        using var registration = token.Register(() => { ended.TrySetCanceled(token); player.Stop(); });
        token.ThrowIfCancellationRequested();
        if (!speech.IsSuspended) player.Play();

        var cues = SpeechTimelineBuilder.BuildEstimated(composition.Text, rate);
        var scale = reader.TotalTime.TotalMilliseconds / Math.Max(1, cues[^1].TimeMilliseconds);
        foreach (var cue in cues)
        {
            var audioPosition = cue.TimeMilliseconds * scale * reader.WaveFormat.AverageBytesPerSecond / 1000;
            while (!ended.Task.IsCompleted && player.GetPosition() < audioPosition)
                await Task.Delay(20, token).ConfigureAwait(false);
            if (ended.Task.IsCompleted) break;
            Report(composition, cue.VisibleCharacters, false, progress);
        }
        await ended.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static void Report(CoreSpeechComposition composition, int visible, bool complete,
        IProgress<CoreSpeechProgress> progress) =>
        progress.Report(new(CoreSpeechTextService.MapVisibleCharacters(composition, visible), complete, false));

    public void Cancel()
    {
        lock (_sync) _active?.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _active?.Cancel();
        }
    }
}
