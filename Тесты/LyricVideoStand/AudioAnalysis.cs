using System.Buffers.Binary;
using System.IO;
using NAudio.Dsp;

namespace LyricVideoStand;

internal readonly record struct AudioEnergy(float Level, float Bass, float Mid, float High, float Attack);
internal sealed record AnalysisPoint(double Time, float Rms, float Bass, float Mid, float High, AudioEnergy Energy);

internal sealed class AudioAnalysis
{
    public const int Rate = 48000;
    public const int Channels = 2;
    private const int Window = 2048;
    private const int Hop = 960;
    public required double Duration { get; init; }
    public required AnalysisPoint[] Points { get; init; }
    public required float Rms95 { get; init; }
    public double[] Impacts => Points.Where(p => p.Energy.Attack > .12f).Select(p => p.Time).ToArray();
    public AudioEnergy At(double time)
    {
        if (time < 0 || time >= Duration || Points.Length == 0) return default;
        // Analysis windows are centred on their true sample position.
        var index = Math.Clamp((time * Rate - Window / 2d) / Hop, 0, Points.Length - 1d);
        var a = (int)index; var b = Math.Min(Points.Length - 1, a + 1); var x = (float)(index - a);
        var left = Points[a].Energy; var right = Points[b].Energy;
        float Mix(float v, float w) => v + (w - v) * x;
        return new(Mix(left.Level, right.Level), Mix(left.Bass, right.Bass), Mix(left.Mid, right.Mid),
            Mix(left.High, right.High), Mix(left.Attack, right.Attack));
    }
    public static AudioAnalysis ReadPcm(string path, CancellationToken token)
    {
        using var input = File.OpenRead(path);
        if (input.Length % 4 != 0 || input.Length < Window * 4 || input.Length > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("PCM must contain bounded 48 kHz stereo s16le samples.");
        var duration = input.Length / (double)(Rate * Channels * 2);
        var data = new byte[Window * 4]; var left = new Complex[Window]; var right = new Complex[Window];
        var window = Enumerable.Range(0, Window).Select(i => (float)FastFourierTransform.HannWindow(i, Window)).ToArray();
        var values = new List<(double Time, float Rms, float Bass, float Mid, float High)>();
        var read = input.Read(data); long offset = 0;
        while (read > 0)
        {
            token.ThrowIfCancellationRequested(); var count = read / 4; double squares = 0;
            for (var i = 0; i < Window; i++)
            {
                var l = i < count ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 4)) / 32768f : 0;
                var rr = i < count ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 4 + 2)) / 32768f : 0;
                squares += (l * l + rr * rr) * .5;
                left[i] = new() { X = l * window[i] }; right[i] = new() { X = rr * window[i] };
            }
            // Separate channel powers preserve antiphase audio, unlike a mono sum.
            FastFourierTransform.FFT(true, 11, left); FastFourierTransform.FFT(true, 11, right);
            double bass = 0, mid = 0, high = 0;
            for (var i = 1; i < Window / 2; i++)
            {
                var hz = i * Rate / (double)Window;
                var power = (left[i].X * left[i].X + left[i].Y * left[i].Y + right[i].X * right[i].X + right[i].Y * right[i].Y) * .5;
                if (hz >= 40 && hz < 250) bass += power;
                else if (hz >= 250 && hz < 2000) mid += power;
                else if (hz >= 2000 && hz < 12000) high += power;
            }
            values.Add((Math.Min(duration, (offset + Window / 2d) / Rate), (float)Math.Sqrt(squares / Window),
                (float)Math.Sqrt(bass), (float)Math.Sqrt(mid), (float)Math.Sqrt(high)));
            if (input.Position >= input.Length && count <= Hop) break;
            var retained = Math.Max(0, read - Hop * 4);
            if (retained > 0) Buffer.BlockCopy(data, Hop * 4, data, 0, retained);
            var fresh = input.Read(data, retained, data.Length - retained); read = retained + fresh; offset += Hop;
        }
        float P95(IEnumerable<float> numbers)
        {
            var sorted = numbers.Order().ToArray(); return Math.Max(.0001f, sorted[(int)((sorted.Length - 1) * .95)]);
        }
        var rms95 = P95(values.Select(v => v.Rms)); var b95 = P95(values.Select(v => v.Bass));
        var m95 = P95(values.Select(v => v.Mid)); var h95 = P95(values.Select(v => v.High));
        float smoothed = 0, b = 0, m = 0, h = 0;
        float Smooth(float old, float target) => old + (target - old) * (target > old ? .55f : .09f);
        var points = new List<AnalysisPoint>(values.Count);
        foreach (var v in values)
        {
            var level = v.Rms < .0001f ? 0 : Math.Clamp(v.Rms / rms95, 0, 1.3f);
            var prior = smoothed; smoothed = Smooth(smoothed, level);
            // A shared loudness gate prevents tiny high-frequency noise from looking loud.
            var gate = Math.Clamp(level * 2, 0, 1);
            b = Smooth(b, Math.Clamp(v.Bass / b95, 0, 1.3f) * gate);
            m = Smooth(m, Math.Clamp(v.Mid / m95, 0, 1.3f) * gate);
            h = Smooth(h, Math.Clamp(v.High / h95, 0, 1.3f) * gate);
            points.Add(new(v.Time, v.Rms, v.Bass, v.Mid, v.High, new(smoothed, b, m, h, Math.Max(0, smoothed - prior))));
        }
        return new() { Duration = duration, Points = points.ToArray(), Rms95 = rms95 };
    }
}
