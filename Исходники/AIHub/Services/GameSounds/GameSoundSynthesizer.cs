using System.IO;
using NAudio.Wave;

namespace AIHub.Services.GameSounds;

internal static class GameSoundSynthesizer
{
    internal const string Revision = "lopata-procedural-1";
    internal const int SampleRate = 44100;
    internal static readonly WaveFormat Format = new(SampleRate, 16, 1);

    internal static short[] Render(IReadOnlyList<GameSoundSource> layers, CancellationToken token = default)
    {
        var duration = 0;
        foreach (var layer in layers)
        {
            token.ThrowIfCancellationRequested();
            GameSoundRecipes.Validate(layer.Recipe.Sound);
            if (layer.StartMs < 0 || layer.Level is < 0 or > 100 ||
                layer.StartMs > GameSoundRecipes.MaxMilliseconds - layer.Recipe.Sound.DurationMs)
                throw new InvalidDataException("Layer falls outside the effect timeline.");
            if (layer.Enabled) duration = Math.Max(duration, layer.StartMs + layer.Recipe.Sound.DurationMs);
        }
        if (duration == 0) return [];
        var mix = new double[Samples(duration)];
        foreach (var layer in layers)
        {
            token.ThrowIfCancellationRequested();
            if (layer.Enabled && layer.Level > 0) AddLayer(layer, mix, token);
        }
        var peak = mix.Max(Math.Abs);
        var scale = peak > .92 ? .92 / peak : 1;
        var pcm = new short[mix.Length];
        for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Round(mix[i] * scale * short.MaxValue);
        return pcm;
    }

    private static int Samples(int milliseconds) => (int)((long)milliseconds * SampleRate / 1000);
    private static void AddLayer(GameSoundSource layer, double[] mix, CancellationToken token)
    {
        var sound = layer.Recipe.Sound;
        var offset = Samples(layer.StartMs);
        var count = Samples(layer.StartMs + sound.DurationMs) - offset;
        var attack = Samples(sound.AttackMs);
        var release = Samples(sound.ReleaseMs);
        var phase = 0d;
        var filtered = 0d;
        var state = sound.Seed == 0 ? 1u : sound.Seed;
        var blend = sound.NoisePercent / 100d;
        var level = sound.LevelPercent / 100d * (layer.Level / 100d) * .72;
        var cutoff = 220 * Math.Pow(65, sound.Brightness / 100d);
        var filter = 1 - Math.Exp(-Math.Tau * cutoff / SampleRate);
        var duty = sound.PulseWidth / 100d;
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var progress = i / (double)(count - 1);
            var frequency = sound.StartHz + (sound.EndHz - sound.StartHz) * progress;
            var cents = Math.Sin(Math.Tau * sound.VibratoHz * i / SampleRate) * sound.VibratoCents;
            if (progress >= .38) cents += 100 * sound.ArpeggioSemitones;
            phase = (phase + Math.Min(12000, frequency * Math.Pow(2, cents / 1200)) / SampleRate) % 1;
            var tone = sound.Waveform switch
            {
                "square" => phase < duty ? 2 * (1 - duty) : -2 * duty,
                "saw" => phase * 2 - 1,
                "triangle" => 1 - 4 * Math.Abs(phase - .5),
                _ => Math.Sin(Math.Tau * phase)
            };
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            var noise = state / (double)uint.MaxValue * 2 - 1;
            filtered += filter * (tone * (1 - blend) + noise * blend - filtered);
            var envelope = Math.Min(1, Math.Min(i / (double)attack, (count - 1 - i) / (double)release));
            mix[offset + i] += filtered * envelope * level;
        }
    }

    internal static byte[] ToBytes(short[] pcm)
    {
        var bytes = new byte[checked(pcm.Length * sizeof(short))];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
