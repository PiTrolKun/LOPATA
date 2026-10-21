using System.IO;

namespace AIHub.Services;

/// <summary>Offline waveform-similarity overlap/add. A shared stereo offset avoids channel drift.
/// Only the newly generated layer is processed; this is an approximate musical edit.</summary>
internal static class MusicPcmTimeFit
{
    private const int Rate = 48000, Window = 1920, Hop = Window / 2, Search = 480;

    internal static void Write(string input, string output, MusicLayerFitSummary plan, CancellationToken token)
    {
        var bytes = MusicPcm16.Read(input);
        var samples = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        var inputFrames = samples.Length / 2; var frames = checked((int)Math.Round(plan.OriginalSeconds * Rate));
        if (frames <= 0 || plan.Anchors.Length < 2 || Math.Abs(inputFrames / (double)Rate - plan.FreshSeconds) > .001)
            throw new InvalidDataException("Music.Transform.InvalidInput");
        var result = new float[checked(frames * 2)];
        var weights = Enumerable.Range(0, Window).Select(i => (float)(.5 - .5 * Math.Cos(2 * Math.PI * i / Window))).ToArray();
        var previousOverlap = new double[Hop]; var havePrevious = false; var segment = 0;
        for (var target = -Hop; target < frames; target += Hop) {
            token.ThrowIfCancellationRequested();
            var center = Math.Clamp((target + Hop) / (double)Rate, 0, plan.OriginalSeconds);
            while (segment < plan.Anchors.Length - 2 && center > plan.Anchors[segment + 1].Original) segment++;
            var a = plan.Anchors[segment]; var b = plan.Anchors[segment + 1];
            var position = a.Fresh + (center - a.Original) * (b.Fresh - a.Fresh) / (b.Original - a.Original);
            var wanted = Math.Clamp((int)Math.Round(position * Rate) - Hop, -Hop, inputFrames - Hop);
            var selected = wanted;
            if (havePrevious) {
                double best = double.NegativeInfinity, energy = 0;
                for (var k = 0; k < Hop; k += 8) energy += previousOverlap[k] * previousOverlap[k];
                if (energy > 1) {
                    for (var offset = -Search; offset <= Search; offset += 16) {
                        var candidate = wanted + offset;
                        if (candidate < -Hop || candidate > inputFrames - Hop) continue;
                        double dot = 0, norm = 0;
                        for (var k = 0; k < Hop; k += 8) { var value = Mono(candidate + k); dot += previousOverlap[k] * value; norm += value * value; }
                        var score = dot / Math.Sqrt(energy * norm + 1) - .02 * Math.Abs(offset) / Search;
                        if (score > best) { best = score; selected = candidate; }
                    }
                }
            }
            for (var k = 0; k < Window; k++) {
                var from = selected + k; var to = target + k;
                if (from < 0 || from >= inputFrames || to < 0 || to >= frames) continue;
                result[to * 2] += samples[from * 2] * weights[k]; result[to * 2 + 1] += samples[from * 2 + 1] * weights[k];
            }
            for (var k = 0; k < Hop; k++) previousOverlap[k] = Mono(selected + Hop + k);
            havePrevious = true;
        }
        var converted = new short[result.Length];
        for (var i = 0; i < result.Length; i++) { if ((i & 65535) == 0) token.ThrowIfCancellationRequested(); converted[i] = (short)Math.Clamp(Math.Round(result[i]), short.MinValue, short.MaxValue); }
        var pcm = new byte[checked(converted.Length * 2)]; Buffer.BlockCopy(converted, 0, pcm, 0, pcm.Length);
        token.ThrowIfCancellationRequested(); MusicPcm16.Write(output, pcm);

        double Mono(int frame) => frame < 0 || frame >= inputFrames ? 0 : (samples[frame * 2] + (double)samples[frame * 2 + 1]) * .5;
    }
}
