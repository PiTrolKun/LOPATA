using System.IO;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using NAudio.Wave;

namespace AIHub.Services;

internal sealed class IslandKittenVoice(ManagedModelLibraryStore library) : IDisposable
{
    private const string Model = VideoNarratorCatalog.KittenMini;
    private readonly UpstreamVideoNarrator _narrator = new(Model, library);
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("lopata-island-voice-");

    internal bool IsWarm => _narrator.IsResident;

    internal static bool IsPrepared(ManagedModelLibraryStore library)
    {
        var model = library.Load(VideoNarratorCatalog.ArtifactId(Model));
        if (model is not { Status: ManagedModelStatuses.Installed }) return false;

        var cards = VideoModelPreparation.Cards(model.ModelsRoot, new("speech", [Model]));
        return cards.All(expected =>
        {
            var card = library.Load(expected.ModelArtifactId);
            return card is { Status: ManagedModelStatuses.Installed }
                && Directory.Exists(card.InstallDirectory)
                && VideoNarratorCatalog.MatchesPin(card, expected)
                && card.Files.All(file =>
                {
                    var installed = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
                    return installed.Exists && installed.Length == file.SizeBytes;
                });
        });
    }

    internal async Task WarmAsync(bool russian, CancellationToken token)
    {
        // ONNX loads during the first inference. Keep this directory for the process log.
        using var scope = VideoNarrationWorkspace.Capture(_workspace.FullName, publishOutput: false);
        await _narrator.GenerateAsync(Model, russian ? "ru" : "en", russian ? "Готова." : "Ready.", 100, token);
    }

    internal async Task<bool> SpeakAsync(string text, bool russian, CancellationToken token)
    {
        var reply = _workspace.CreateSubdirectory("reply");
        try
        {
            using var scope = VideoNarrationWorkspace.Capture(reply.FullName, publishOutput: false);
            var audio = await _narrator.GenerateAsync(Model, russian ? "ru" : "en", text, 100, token);
            try
            {
                await PlayAsync(audio.Path, token);
                return true;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OwnedProcessRegistry.Log("island_voice_reply_failed", "Island.MusicVoice", detail: error.GetType().Name);
                return false;
            }
        }
        finally { DeleteWorkspace(reply); }
    }

    private static async Task PlayAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var audio = new WaveFileReader(path);
        using var player = new WaveOutEvent { DesiredLatency = 100 };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is { } error) completed.TrySetException(error);
            else completed.TrySetResult();
        };
        player.Init(audio);
        player.Play();
        try { await completed.Task.WaitAsync(token); }
        finally { player.Stop(); }
    }

    public void Dispose()
    {
        try { _narrator.Dispose(); }
        finally { DeleteWorkspace(_workspace); }
    }

    private static void DeleteWorkspace(DirectoryInfo directory)
    {
        try
        {
            Lopata.Updates.SafeUpdatePath.RejectLinks(directory.FullName);
            directory.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            OwnedProcessRegistry.Log("island_voice_cleanup_failed", "Island.MusicVoice", detail: error.GetType().Name);
        }
    }
}
