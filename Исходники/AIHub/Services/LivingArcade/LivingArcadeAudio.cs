using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AIHub.Services.LivingArcade;

/// <summary>Local PCM synthesis mixed through the existing NAudio WASAPI runtime.</summary>
public sealed class LivingArcadeAudio : IDisposable
{
    public const int SampleRate = 22050;
    private const int MaximumVoices = 12;
    private readonly Dictionary<string, byte[]> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPlayed = new(StringComparer.Ordinal);
    private WasapiOut? _output;
    private PcmMixer? _mixer;
    private byte[]? _musicData;
    private Exception? _playbackFailure;
    private float _effectsVolume = .45f;
    private float _musicVolume = .16f;
    private bool _unavailable;
    private bool _disposed;
    private bool _paused = true;

    public event Action<Exception>? AudioFailed;
    public bool IsAvailable => !_unavailable && !_disposed;
    public bool HasMusic => _musicData is not null;
    public float EffectsVolume
    {
        get => _effectsVolume;
        set { _effectsVolume = Volume(value); _mixer?.SetVolumes(_effectsVolume, _musicVolume); }
    }
    public float MusicVolume
    {
        get => _musicVolume;
        set { _musicVolume = Volume(value); _mixer?.SetVolumes(_effectsVolume, _musicVolume); }
    }

    public void Play(string cue)
    {
        if (_disposed || _unavailable || _paused || _effectsVolume <= 0) return;
        if (cue is not ("shoot" or "hit" or "enemy_down" or "bonus" or "negative" or "boss" or "boss_phase"
            or "speed_up" or "slow_down" or "super" or "defeat" or "complete")) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var minimumInterval = cue == "shoot" ? .045 : cue == "hit" ? .04 : .065;
            if (_lastPlayed.TryGetValue(cue, out var previous) && Stopwatch.GetElapsedTime(previous, now).TotalSeconds < minimumInterval) return;
            _lastPlayed[cue] = now;
            if (!_effects.TryGetValue(cue, out var data)) { data = SynthesizeEffect(cue); _effects.Add(cue, data); }
            EnsureOutput();
            _mixer!.Add(data, cue is "defeat" or "complete",
                cue is "super" or "defeat" or "complete" or "bonus" or "negative" or "boss" or "boss_phase");
            if (_output!.PlaybackState != PlaybackState.Playing) _output.Play();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Disable(error); }
    }

    public void StartMusic(int seed, IReadOnlyList<int>? notes = null)
    {
        if (_disposed || _unavailable) return;
        _musicData = SynthesizeMusic(seed, notes);
        _mixer?.SetMusic(_musicData);
        if (!_paused) Resume();
    }

    public void Resume()
    {
        if (_disposed || _unavailable) return;
        _paused = false;
        try
        {
            _musicData ??= SynthesizeMusic(0);
            EnsureOutput(); _mixer!.SetPaused(false, false);
            _output!.Play();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Disable(error); }
    }

    public void Pause(bool preserveEndingCue = false)
    {
        _paused = true;
        if (_disposed || _unavailable) return;
        try
        {
            var ending = _mixer?.SetPaused(true, preserveEndingCue) == true;
            if (!ending && _output?.PlaybackState == PlaybackState.Playing) _output.Pause();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Disable(error); }
    }

    public void Update()
    {
        if (_disposed || _unavailable) return;
        var error = Interlocked.Exchange(ref _playbackFailure, null);
        if (error is not null) Disable(error);
    }

    private void EnsureOutput()
    {
        if (_output is not null) return;
        _mixer = new PcmMixer();
        _mixer.SetVolumes(_effectsVolume, _musicVolume);
        _mixer.SetMusic(_musicData);
        _mixer.SetPaused(_paused, false);
        _output = new WasapiOut(AudioClientShareMode.Shared, true, 70);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_mixer);
    }
    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    { if (args.Exception is not null) Interlocked.Exchange(ref _playbackFailure, args.Exception); }
    private void Disable(Exception error)
    {
        if (_unavailable) return;
        _unavailable = true; ReleaseAudio(); AudioFailed?.Invoke(error);
    }
    private void ReleaseAudio()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch (Exception) { /* The endpoint can already be unavailable. */ }
            try { _output.Dispose(); } catch (Exception) { /* Same endpoint failure during disposal. */ }
            _output = null;
        }
        _mixer = null; _musicData = null; _effects.Clear();
    }
    public void Dispose()
    { if (_disposed) return; _disposed = true; _paused = true; ReleaseAudio(); }

    /// <summary>The audio callback owns playback cursors; UI commands only mutate under this short lock.</summary>
    internal sealed class PcmMixer : IWaveProvider
    {
        private sealed class Voice(byte[] data, bool ending)
        { public readonly byte[] Data = data; public readonly bool Ending = ending; public int Position; }
        private readonly object _gate = new();
        private readonly List<Voice> _voices = [];
        private byte[]? _music;
        private int _musicPosition;
        private float _effects = .45f, _musicGain = .16f;
        private bool _paused = true;
        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);
        public void SetVolumes(float effects, float music) { lock (_gate) { _effects = effects; _musicGain = music; } }
        public void SetMusic(byte[]? data) { lock (_gate) { _music = data; _musicPosition = 0; } }
        public bool SetPaused(bool paused, bool preserveEnding)
        {
            lock (_gate)
            {
                _paused = paused;
                if (paused) _voices.RemoveAll(v => !preserveEnding || !v.Ending);
                return _voices.Count > 0;
            }
        }
        public void Add(byte[] data, bool ending, bool important)
        {
            lock (_gate)
            {
                if (_voices.Count >= MaximumVoices)
                { if (!important) return; _voices.RemoveAt(0); }
                _voices.Add(new(data, ending));
            }
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                Array.Clear(buffer, offset, count);
                for (var i = 0; i + 1 < count; i += 2)
                {
                    double sample = 0;
                    if (!_paused && _music is { Length: >= 2 })
                    {
                        if (_musicPosition >= _music.Length) _musicPosition = 0;
                        sample = BinaryPrimitives.ReadInt16LittleEndian(_music.AsSpan(_musicPosition, 2)) * _musicGain;
                        _musicPosition += 2;
                    }
                    for (var voice = _voices.Count - 1; voice >= 0; voice--)
                    {
                        var v = _voices[voice];
                        if (v.Position + 1 >= v.Data.Length) { _voices.RemoveAt(voice); continue; }
                        sample += BinaryPrimitives.ReadInt16LittleEndian(v.Data.AsSpan(v.Position, 2)) * _effects;
                        v.Position += 2;
                    }
                    // Soft saturation protects the endpoint when several bounded effects overlap.
                    var normalized = sample / short.MaxValue;
                    var mixed = Math.Tanh(normalized);
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i, 2), (short)(mixed * short.MaxValue));
                }
                return count;
            }
        }
    }
    private static float Volume(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public static byte[] SynthesizeEffect(string cue)
    {
        var (duration, frequency, endFrequency, noise) = cue switch
        {
            "shoot" => (.075, 1050d, 330d, .08),
            "hit" => (.10, 180d, 75d, .70),
            "enemy_down" => (.18, 260d, 45d, .65),
            "bonus" => (.28, 440d, 1760d, .02),
            "negative" => (.24, 650d, 170d, .28),
            "boss" => (.55, 90d, 220d, .22),
            "boss_phase" => (.25, 220d, 660d, .15),
            "speed_up" => (.32, 180d, 880d, .02),
            "slow_down" => (.32, 880d, 180d, .02),
            "super" => (.43, 120d, 35d, .85),
            "defeat" => (.70, 430d, 55d, .15),
            "complete" => (.65, 440d, 1320d, .02),
            _ => (.12, 440d, 220d, .04)
        };
        var count = (int)(SampleRate * duration);
        var bytes = new byte[count * 2];
        uint state = 0x51A7E123;
        double phase = 0;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)SampleRate;
            var progress = t / duration;
            var current = frequency + (endFrequency - frequency) * progress;
            if (cue is "bonus" or "complete") current *= Math.Min(3, (int)(progress * 4)) switch { 1 => 1.25, 2 => 1.5, 3 => 2, _ => 1 };
            phase += current / SampleRate;
            var pulse = phase % 1 < .35 ? 1d : -.6;
            var random = NextNoise(ref state);
            var envelope = Math.Min(1, t / .004) * Math.Pow(1 - progress, 1.5);
            WriteSample(bytes, i, (pulse * (1 - noise) + random * noise) * envelope * .46);
        }
        return bytes;
    }

    public static byte[] SynthesizeMusic(int seed, IReadOnlyList<int>? notes = null)
    {
        int[] melody = notes is { Count: > 0 and <= 64 } && notes.All(n => n is >= 36 and <= 96)
            ? notes.ToArray() : [69, 72, 76, 79, 76, 72, 74, 71, 69, 72, 76, 81, 79, 76, 74, 72];
        const double stepSeconds = .22;
        var steps = Math.Max(32, melody.Length);
        var count = (int)(SampleRate * stepSeconds * steps);
        var bytes = new byte[count * 2];
        var root = melody[0] - 24;
        var transpose = (int)((uint)seed % 3) - 1;
        uint noiseState = (uint)seed ^ 0xA3E45F17;
        if (noiseState == 0) noiseState = 1;
        double leadPhase = 0, bassPhase = 0;
        for (var i = 0; i < count; i++)
        {
            var time = i / (double)SampleRate;
            var step = Math.Min(steps - 1, (int)(time / stepSeconds));
            var local = time - step * stepSeconds;
            var note = melody[step % melody.Length] + transpose;
            leadPhase += MidiFrequency(note) / SampleRate;
            var lead = (leadPhase % 1 < .25 ? 1 : -.333) * Math.Min(1, local / .006) * Math.Exp(-local * 10);
            var bassNote = root + ((step / 8 % 4) switch { 1 => 5, 2 => 3, 3 => 7, _ => 0 }) + transpose;
            bassPhase += MidiFrequency(bassNote) / SampleRate;
            var bass = (4 * Math.Abs(bassPhase % 1 - .5) - 1) * Math.Exp(-(time % (stepSeconds * 2)) * 4);
            var percussion = NextNoise(ref noiseState) * Math.Exp(-local * 65) * (step % 4 == 2 ? .17 : .065);
            var kick = Math.Sin(2 * Math.PI * (65 * local - 60 * local * local)) * Math.Exp(-local * 35) * (step % 4 == 0 ? .24 : 0);
            var edgeFade = Math.Min(1, Math.Min(time / .012, (count - i) / (SampleRate * .012)));
            WriteSample(bytes, i, (lead * .20 + bass * .22 + percussion + kick) * edgeFade);
        }
        return bytes;
    }

    private static double MidiFrequency(int note) => 440 * Math.Pow(2, (note - 69) / 12d);
    private static double NextNoise(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state / (double)uint.MaxValue * 2 - 1;
    }

    private static void WriteSample(byte[] buffer, int sample, double value) =>
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(sample * 2, 2),
            (short)Math.Clamp((int)(value * short.MaxValue), short.MinValue, short.MaxValue));
}
