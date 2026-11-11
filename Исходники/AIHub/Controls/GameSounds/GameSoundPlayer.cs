using System.IO;
using AIHub.Services.GameSounds;
using NAudio.Wave;

namespace AIHub.Controls.GameSounds;

internal sealed class GameSoundPlayer : IDisposable
{
    private CancellationTokenSource? _rendering;
    private byte[]? _prepared;
    private IWavePlayer? _output;
    private PlaybackStream? _stream;
    internal bool Playing => _output?.PlaybackState == PlaybackState.Playing;
    internal event Action? PlaybackChanged;

    internal async Task PrepareAsync(IReadOnlyList<GameSoundSource> sources, CancellationToken token)
    {
        _rendering?.Cancel();
        using var render = CancellationTokenSource.CreateLinkedTokenSource(token);
        _rendering = render;
        try
        {
            var bytes = await Task.Run(() => GameSoundSynthesizer.ToBytes(GameSoundSynthesizer.Render(sources, render.Token)), render.Token);
            render.Token.ThrowIfCancellationRequested();
            _prepared = bytes;
        }
        finally { if (ReferenceEquals(_rendering, render)) _rendering = null; }
    }

    internal async Task PlayAsync(IReadOnlyList<GameSoundSource> sources, bool loop, CancellationToken token)
    {
        if (_output?.PlaybackState == PlaybackState.Paused && _stream is not null)
        { _stream.Loop = loop; _output.Play(); PlaybackChanged?.Invoke(); return; }
        Stop();
        if (_prepared is null) await PrepareAsync(sources, token);
        PlayPrepared(loop);
    }

    internal async Task PlayFileAsync(string path, CancellationToken token)
    {
        Invalidate();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(token);
        _rendering = load;
        try
        {
            var bytes = await GameSoundAudio.ReadPcmAsync(path, load.Token);
            load.Token.ThrowIfCancellationRequested();
            _prepared = bytes;
            PlayPrepared(loop: false);
        }
        finally { if (ReferenceEquals(_rendering, load)) _rendering = null; }
    }

    private void PlayPrepared(bool loop)
    {
        if (_prepared is not { Length: > 0 }) throw new InvalidDataException("GameSounds.NoLayers");
        _stream = new(_prepared) { Loop = loop };
        var output = new WaveOutEvent { DesiredLatency = 100 };
        _output = output;
        output.PlaybackStopped += (_, e) =>
        {
            if (!ReferenceEquals(_output, output)) return;
            PlaybackChanged?.Invoke();
        };
        try { output.Init(_stream); output.Play(); PlaybackChanged?.Invoke(); }
        catch { Stop(); throw; }
    }

    internal void Pause() { _output?.Pause(); PlaybackChanged?.Invoke(); }
    internal void SetLoop(bool loop) { if (_stream is not null) _stream.Loop = loop; }
    internal void Stop()
    {
        var output = _output;
        _output = null;
        output?.Dispose();
        _stream?.Dispose(); _stream = null;
        PlaybackChanged?.Invoke();
    }
    internal void Invalidate() { _rendering?.Cancel(); _prepared = null; Stop(); }
    public void Dispose() => Invalidate();

    private sealed class PlaybackStream(byte[] pcm) : WaveStream
    {
        private volatile bool _loop;
        internal bool Loop { get => _loop; set => _loop = value; }
        public override WaveFormat WaveFormat => GameSoundSynthesizer.Format;
        public override long Length => pcm.Length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                if (Position == Length)
                {
                    if (!Loop) break;
                    Position = 0;
                }
                var available = (int)Math.Min(count - written, Length - Position);
                Buffer.BlockCopy(pcm, (int)Position, buffer, offset + written, available);
                Position += available; written += available;
            }
            return written;
        }
    }
}
