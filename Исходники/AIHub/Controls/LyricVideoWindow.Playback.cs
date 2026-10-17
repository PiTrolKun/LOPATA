using System.Diagnostics;
using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LyricVideoWindow
{
    internal double Time => _credits ? _creditsBase + (_playing ? _creditsClock.Elapsed.TotalSeconds : 0) : _player.Position.TotalSeconds;
    internal void Toggle()
    {
        if (!_ready || _session is null) return;
        if (Time >= _session.TotalDuration - .02) Seek(0);
        if (_playing)
        {
            if (_credits) { _creditsBase = Time; _creditsClock.Reset(); } else _player.Pause();
        }
        else { if (_credits) _creditsClock.Restart(); else _player.Play(); }
        _playing = !_playing; _play.Content = _playing ? _l("LyricVideo.Pause") : _l("LyricVideo.Play");
    }
    internal void Seek(double position)
    {
        if (!_ready || _session is null) return;
        position = Math.Clamp(position, 0, _session.TotalDuration); _player.Pause(); _creditsClock.Reset();
        _credits = position >= _session.SongDuration;
        if (_credits) { _creditsBase = position; if (_playing) _creditsClock.Start(); }
        else { _player.Position = TimeSpan.FromSeconds(position); if (_playing) _player.Play(); }
        _lastFrame = -1; RequestFrame();
    }
    private void RequestFrame()
    {
        if (!_ready || _session is null || _renderer is null || _closing || _frameWork is { IsCompleted: false }) return;
        var t = Math.Clamp(Time, 0, _session.TotalDuration);
        if (t >= _session.TotalDuration && _playing)
        {
            _playing = false; _creditsBase = _session.TotalDuration; _creditsClock.Reset();
            _play.Content = _l("LyricVideo.Play");
        }
        if (Math.Abs(t - _lastFrame) < 1d / 120) return;
        _lastFrame = t; _frameWork = DrawAsync(t);
    }
    private async Task DrawAsync(double time)
    {
        try
        {
            var pixels = await Task.Run(() => _renderer!.Render(time));
            if (_closing) return;
            _bitmap!.WritePixels(new Int32Rect(0, 0, 1920, 1080), pixels, 1920 * 4, 0);
            _clock.Text = Format(time) + " / " + Format(_session!.TotalDuration);
            if (!_dragging) { _updatingSeek = true; _seek.Value = time; _updatingSeek = false; }

        }
        catch (Exception error) { if (!_closing) { _timer.Stop(); _player.Pause(); OwnedProcessRegistry.Log("clip_frame_failed", "Music.Video", detail: error.GetType().Name); _status.Text = _l("LyricVideo.Failed"); } }
    }
    private static string Format(double time) => TimeSpan.FromSeconds(time).ToString(@"mm\:ss");
}
