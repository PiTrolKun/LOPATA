using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIHub.Services;

public interface IMusicAudioPlayer : IDisposable
{
    bool IsReady { get; }
    bool IsPlaying { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    double Volume { get; set; }
    event Action? Changed;
    event Action<string>? Failed;
    void Open(string? path);
    void Toggle();
    void Pause();
    void Seek(TimeSpan position);
}

/// <summary>Music playback owns the decoder, never the source file.</summary>
public sealed class MusicAudioPlayer : IMusicAudioPlayer
{
    private MediaPlayer? _player;
    private bool _playWhenReady, _disposed;
    private CancellationTokenSource? _opening;
    private string? _temporary;
    private double _volume = .75;
    public bool IsReady { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration => IsReady && _player!.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public TimeSpan Position => IsReady ? _player!.Position : TimeSpan.Zero;
    public double Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 1); if (_player is not null) _player.Volume = _volume; } }
    public event Action? Changed;
    public event Action<string>? Failed;
    public void Open(string? path)
    {
        if (_disposed) return;
        _opening?.Cancel(); _opening = null;
        var previous = _player; _player = null; previous?.Close();
        DeleteTemporary(_temporary); _temporary = null;
        IsReady = IsPlaying = _playWhenReady = false;
        Changed?.Invoke();
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { Failed?.Invoke("Music.Audio.Missing"); return; }
        if (Path.GetExtension(path).ToLowerInvariant() is ".opus" or ".mp3" or ".flac") {
            var cancellation = new CancellationTokenSource(); _opening = cancellation;
            _ = DecodeAndOpenAsync(path, cancellation, Dispatcher.CurrentDispatcher); return;
        }
        OpenMedia(path);
    }
    private async Task DecodeAndOpenAsync(string source, CancellationTokenSource cancellation, Dispatcher dispatcher)
    {
        var folder = Path.Combine(AppDataPaths.BaseDirectory, "Music", "Playback");
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav");
        try {
            Directory.CreateDirectory(folder);
            await MusicAudioRuntime.Default.DecodeAsync(source, temporary, cancellation.Token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => {
                if (_disposed || _opening != cancellation) return;
                _opening = null; _temporary = temporary; OpenMedia(temporary); temporary = "";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException) {
            if (!dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => {
                if (_opening != cancellation || _disposed) return; _opening = null; _playWhenReady = false;
                Failed?.Invoke("Music.Audio.Error"); Changed?.Invoke();
            });
        }
        finally {
            if (!dispatcher.HasShutdownStarted) await dispatcher.InvokeAsync(() => {
                if (_opening == cancellation) { _opening = null; _playWhenReady = false; if (!_disposed) Changed?.Invoke(); }
            });
            DeleteTemporary(temporary); cancellation.Dispose();
        }
    }
    private void OpenMedia(string path)
    {
        var player = new MediaPlayer { Volume = _volume }; _player = player;
        player.MediaOpened += (_, _) =>
        {
            if (_player != player) return;
            IsReady = player.NaturalDuration.HasTimeSpan;
            if (_playWhenReady && IsReady) { player.Play(); IsPlaying = true; }
            _playWhenReady = false; Changed?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        { if (_player != player) return; player.Pause(); player.Position = TimeSpan.Zero; IsPlaying = false; Changed?.Invoke(); };
        player.MediaFailed += (_, _) =>
        { if (_player != player) return; _player = null; player.Close(); IsReady = IsPlaying = _playWhenReady = false; Failed?.Invoke("Music.Audio.Error"); Changed?.Invoke(); };
        try { player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute)); }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { _player = null; player.Close(); Failed?.Invoke("Music.Audio.Error"); }
    }
    public void Toggle()
    {
        if (_disposed) return;
        if (_opening is not null) { _playWhenReady = !_playWhenReady; return; }
        if (_player is null) return;
        if (!IsReady) { _playWhenReady = !_playWhenReady; return; }
        if (IsPlaying) _player.Pause(); else _player.Play();
        IsPlaying = !IsPlaying; Changed?.Invoke();
    }
    public void Pause()
    { _playWhenReady = false; _player?.Pause(); IsPlaying = false; Changed?.Invoke(); }
    public void Seek(TimeSpan position)
    { if (IsReady) _player!.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds)); }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _opening?.Cancel(); _opening = null; _player?.Close(); _player = null;
        DeleteTemporary(_temporary); _temporary = null; IsReady = IsPlaying = false; Changed = null; Failed = null; }
    private static void DeleteTemporary(string? path)
    { if (string.IsNullOrEmpty(path)) return; try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
