using System.IO;
using System.Windows.Media;

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
        var previous = _player; _player = null; previous?.Close();
        IsReady = IsPlaying = _playWhenReady = false;
        Changed?.Invoke();
        if (string.IsNullOrEmpty(path)) return;
        if (!File.Exists(path)) { Failed?.Invoke("Music.Audio.Missing"); return; }
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
        if (_disposed || _player is null) return;
        if (!IsReady) { _playWhenReady = !_playWhenReady; return; }
        if (IsPlaying) _player.Pause(); else _player.Play();
        IsPlaying = !IsPlaying; Changed?.Invoke();
    }
    public void Pause()
    { _playWhenReady = false; _player?.Pause(); IsPlaying = false; Changed?.Invoke(); }
    public void Seek(TimeSpan position)
    { if (IsReady) _player!.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds)); }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _player?.Close(); _player = null; IsReady = IsPlaying = false; Changed = null; Failed = null; }
}
