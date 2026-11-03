using System.Windows.Media;

namespace AIHub.Services;

/// <summary>One UI-thread player; stopping completes the wait before another message can start.</summary>
internal sealed class ChatSpeechPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private TaskCompletionSource? _ended;
    internal ChatSpeechPlayer()
    {
        _player.MediaOpened += (_, _) => OwnedProcessRegistry.Log("chat_speech_media_opened", "LetItOut.Speech");
        _player.MediaEnded += (_, _) => { OwnedProcessRegistry.Log("chat_speech_media_ended", "LetItOut.Speech"); _ended?.TrySetResult(); };
        _player.MediaFailed += (_, e) => { OwnedProcessRegistry.Log("chat_speech_media_failed", "LetItOut.Speech", detail: e.ErrorException.GetType().Name); _ended?.TrySetException(e.ErrorException); };
    }
    internal async Task PlayAsync(ChatSpeechAudio audio, CancellationToken token)
    {
        foreach (var file in audio.Files)
        {
            token.ThrowIfCancellationRequested();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _ended = ended;
            _player.Open(new Uri(file)); _player.Play();
            try { await ended.Task.WaitAsync(token); }
            finally { if (ReferenceEquals(_ended, ended)) Stop(); }
        }
    }
    internal void Stop() { _player.Stop(); _player.Close(); _ended?.TrySetCanceled(); _ended = null; }
    public void Dispose() => Stop();
}
