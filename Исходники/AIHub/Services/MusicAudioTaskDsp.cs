using System.Buffers.Binary;
using System.IO;

namespace AIHub.Services;

/// <summary>Read-only source, bounded PCM work. Splice restores original samples outside the explicitly expanded boundary.</summary>
internal static class MusicAudioTaskDsp
{
    internal static void Splice(string source, string generated, string output, double start, double end, double fade, CancellationToken token)
    {
        var original = MusicPcm16.Read(source); var fresh = MusicPcm16.Read(generated);
        if (Math.Abs(original.Length - fresh.Length) > 48000 * 4 * .25) throw new InvalidDataException("Music.Transform.AlignmentFailed");
        var frames = original.Length / 4; var left = Math.Clamp((int)(start * 48000), 0, frames);
        var right = Math.Clamp((int)(end * 48000), left, frames); var blend = (int)(fade * 48000);
        for (var frame = Math.Max(0, left - blend); frame < Math.Min(frames, right + blend); frame++) {
            if ((frame & 16383) == 0) token.ThrowIfCancellationRequested();
            var amount = frame < left ? (double)(frame - left + blend) / Math.Max(1, blend)
                : frame >= right ? (double)(right + blend - frame) / Math.Max(1, blend) : 1;
            for (var channel = 0; channel < 2; channel++) {
                var offset = frame * 4 + channel * 2;
                if (offset + 2 > fresh.Length) throw new InvalidDataException("Music.Transform.AlignmentFailed");
                var a = BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(offset, 2)); var b = BinaryPrimitives.ReadInt16LittleEndian(fresh.AsSpan(offset, 2));
                BinaryPrimitives.WriteInt16LittleEndian(original.AsSpan(offset, 2), (short)Math.Clamp(Math.Round(a * (1 - amount) + b * amount), short.MinValue, short.MaxValue));
            }
        }
        MusicPcm16.Write(output, original);
    }
    internal static void Restore(string source, string output, bool gate, bool normalize, CancellationToken token)
    {
        var data = MusicPcm16.Read(source); double peak = 1;
        // Conservative stereo-linked soft gate, not generative restoration or recovery of lost peaks.
        double gain = 1;
        for (var i = 0; i < data.Length; i += 4) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var level = Math.Max(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2))), Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i + 2, 2))));
            var wanted = !gate || level >= 180 ? 1 : .15 + .85 * level / 180;
            gain += (wanted > gain ? .015 : .0002) * (wanted - gain);
            for (var c = 0; c < 2; c++) { var span = data.AsSpan(i + c * 2, 2); var sample = (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(span) * gain); BinaryPrimitives.WriteInt16LittleEndian(span, sample); peak = Math.Max(peak, Math.Abs((int)sample)); }
        }
        if (normalize) for (var i = 0; i < data.Length; i += 2) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var span = data.AsSpan(i, 2); BinaryPrimitives.WriteInt16LittleEndian(span, (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(span) * (29000 / peak)));
        }
        MusicPcm16.Write(output, data);
    }
    internal static void MixKeeping(string kept, string fresh, string output, CancellationToken token, bool limitVocalLocally = false)
    {
        var original = MusicPcm16.Read(kept); var layer = MusicPcm16.Read(fresh);
        if (Math.Abs(original.Length - layer.Length) > 48000 * 4 * .25) throw new InvalidDataException("Music.Transform.AlignmentFailed");
        var length = Math.Min(original.Length, layer.Length);
        if (limitVocalLocally)
        {
            MixVocalWithLocalHeadroom(original, layer, length, token);
            LeavePeakMargin(original, token);
        }
        else
        {
            var gain = MeasureHeadroom(original, layer, 0, length, token);
            if (gain < .05) throw new InvalidDataException("Music.Transform.HeadroomFailed");
            MixRange(original, layer, 0, length, gain, gain, token);
        }
        MusicPcm16.Write(output, original);
    }

    private static void LeavePeakMargin(byte[] mix, CancellationToken token)
    {
        const int outputPeak = 27570; // -1.5 dBFS, leaving room for peaks during playback and lossy encoding.
        var peak = 0;
        for (var i = 0; i < mix.Length; i += 2)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(mix.AsSpan(i, 2))));
        }
        if (peak <= outputPeak) return;

        // A common gain preserves the vocal-to-instrumental balance and the stereo image.
        var gain = (double)outputPeak / peak;
        for (var i = 0; i < mix.Length; i += 2)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var sample = mix.AsSpan(i, 2);
            BinaryPrimitives.WriteInt16LittleEndian(sample, (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(sample) * gain));
        }
    }

    private static void MixVocalWithLocalHeadroom(byte[] music, byte[] voice, int length, CancellationToken token)
    {
        const int blockBytes = 240 * 4; // Five milliseconds of stereo PCM at 48 kHz.
        const double recoveryPerBlock = .125; // About 40 ms to recover from zero gain.
        var limit = MeasureHeadroom(music, voice, 0, Math.Min(blockBytes, length), token);
        var gain = limit;
        for (var start = 0; start < length;)
        {
            token.ThrowIfCancellationRequested();
            var end = start + Math.Min(blockBytes, length - start);
            var nextEnd = end + Math.Min(blockBytes, length - end);
            var nextLimit = MeasureHeadroom(music, voice, end, nextEnd, token);
            // Start reducing before the next peak. Both stereo channels follow the same gain.
            var nextGain = Math.Min(Math.Min(limit, nextLimit), gain + recoveryPerBlock);
            MixRange(music, voice, start, end, gain, nextGain, token);
            gain = nextGain;
            limit = nextLimit;
            start = end;
        }
    }

    private static double MeasureHeadroom(byte[] original, byte[] layer, int start, int end, CancellationToken token)
    {
        double gain = 1;
        for (var i = start; i < end; i += 2) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var a = BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(i, 2)); var b = BinaryPrimitives.ReadInt16LittleEndian(layer.AsSpan(i, 2));
            if (b > 0) gain = Math.Min(gain, (32760d - a) / b);
            else if (b < 0) gain = Math.Min(gain, (-32760d - a) / b);
        }
        return Math.Clamp(gain, 0, 1);
    }

    private static void MixRange(byte[] original, byte[] layer, int start, int end, double from, double to, CancellationToken token)
    {
        var increment = (to - from) / Math.Max(1, (end - start) / 4);
        for (var i = start; i < end; i += 4) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var gain = from + ((i - start) / 4) * increment;
            for (var channel = 0; channel < 4; channel += 2)
            {
                var sample = original.AsSpan(i + channel, 2);
                var kept = BinaryPrimitives.ReadInt16LittleEndian(sample);
                var added = BinaryPrimitives.ReadInt16LittleEndian(layer.AsSpan(i + channel, 2));
                BinaryPrimitives.WriteInt16LittleEndian(sample, (short)Math.Clamp(kept + Math.Round(added * gain), short.MinValue, short.MaxValue));
            }
        }
    }
}
