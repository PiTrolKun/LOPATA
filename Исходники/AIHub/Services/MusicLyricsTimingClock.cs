using System.Buffers.Binary;
using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Verifies duplicate-format clocks against the actual decoded waveform, not container start_time alone.</summary>
public sealed class MusicLyricsTimingClock(MusicAudioRuntime runtime)
{
    private const int SampleRate = 8000;
    public async Task<MusicTimedLyrics?> MapAsync(string primaryPath, string additionalPath, MusicTimedLyrics timing,
        string temporaryDirectory, CancellationToken token)
    {
        timing.Validate();
        if (timing.AudioPayloadHash != await MusicLyricsTimingMetadata.AudioPayloadHashAsync(primaryPath, token))
            throw new InvalidDataException("Primary timing clock source changed.");
        await runtime.PrepareAsync(token); Directory.CreateDirectory(temporaryDirectory);
        var primaryPcm = Path.Combine(temporaryDirectory, "clock-" + Guid.NewGuid().ToString("N") + ".wav");
        var additionalPcm = Path.Combine(temporaryDirectory, "clock-" + Guid.NewGuid().ToString("N") + ".wav");
        try {
            await Decode(primaryPath, primaryPcm); await Decode(additionalPath, additionalPcm);
            var a = ReadPcm(primaryPcm); var b = ReadPcm(additionalPcm);
            if (Math.Abs(a.Length - b.Length) > SampleRate / 2 || Math.Min(a.Length, b.Length) < SampleRate * 4) return null;
            var comparison = await Task.Run(() => Compare(a, b, token), token);
            if (comparison is null) return null;
            var (offset, score, spread) = comparison.Value; var duration = b.Length / (double)SampleRate;
            var lines = timing.Lines.Where(line => line.StartSeconds + offset >= 0 && line.EndSeconds + offset <= duration)
                .Select(line => line with { StartSeconds = line.StartSeconds + offset, EndSeconds = line.EndSeconds + offset,
                    Words = line.Words.Select(word => word with { StartSeconds = word.StartSeconds + offset, EndSeconds = word.EndSeconds + offset }).ToArray() }).ToArray();
            var result = timing with { AudioPayloadHash = await MusicLyricsTimingMetadata.AudioPayloadHashAsync(additionalPath, token),
                DurationSeconds = duration, Lines = lines, Clock = new(timing.AudioPayloadHash, offset, score, spread) };
            result.Validate(); return result;
        }
        finally { if (File.Exists(primaryPcm)) File.Delete(primaryPcm); if (File.Exists(additionalPcm)) File.Delete(additionalPcm); }
        Task<string> Decode(string source, string destination) => runtime.RunAsync(false,
            ["-nostdin", "-v", "error", "-xerror", "-n", "-i", source, "-map", "0:a:0", "-map_metadata", "-1",
             "-c:a", "pcm_s16le", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", "1", "-f", "wav", destination], token);
    }
    private static short[] ReadPcm(string path)
    {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
        var header = reader.ReadBytes(12); if (!header.AsSpan().StartsWith("RIFF"u8) || !header.AsSpan(8).StartsWith("WAVE"u8)) throw new InvalidDataException("Invalid clock PCM.");
        while (file.Position + 8 <= file.Length) {
            var kind = reader.ReadBytes(4); var length = reader.ReadUInt32();
            if (length > file.Length - file.Position || length > 128 * 1024 * 1024) throw new InvalidDataException("Clock PCM exceeds supported song size.");
            if (kind.AsSpan().SequenceEqual("data"u8)) {
                if ((length & 1) != 0) throw new InvalidDataException("Invalid clock PCM samples.");
                var bytes = reader.ReadBytes((int)length); var samples = new short[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)); return samples;
            }
            file.Position += length + (length & 1);
        }
        throw new InvalidDataException("Missing clock PCM samples.");
    }
    private static (double Offset, double Score, double Spread)? Compare(short[] a, short[] b, CancellationToken token)
    {
        var duration = Math.Min(a.Length, b.Length); var matches = new List<(int Lag, double Score)>();
        var halfWindow = SampleRate * 3 / 4;
        foreach (var fraction in new[] { .15, .325, .5, .675, .85 }) {
            token.ThrowIfCancellationRequested();
            var center = (int)(duration * fraction); center = Math.Clamp(center, halfWindow + SampleRate, duration - halfWindow - SampleRate - 1);
            var bestLag = 0; var best = -1d;
            for (var lag = -SampleRate; lag <= SampleRate; lag += 8) {
                var score = Correlation(a, b, center - halfWindow, halfWindow * 2, lag, 8);
                if (score > best) { best = score; bestLag = lag; }
            }
            var coarse = bestLag;
            best = -1;
            for (var lag = coarse - 8; lag <= coarse + 8; lag++) {
                if (Math.Abs(lag) > SampleRate) continue;
                var score = Correlation(a, b, center - halfWindow, halfWindow * 2, lag, 1);
                if (score > best) { best = score; bestLag = lag; }
            }
            if (best >= .9) matches.Add((bestLag, best));
        }
        if (matches.Count < 3) return null;
        var spread = (matches.Max(m => m.Lag) - matches.Min(m => m.Lag)) / (double)SampleRate;
        if (spread > .03) return null;
        var median = matches.OrderBy(m => m.Lag).ElementAt(matches.Count / 2).Lag;
        return (median / (double)SampleRate, matches.Min(m => m.Score), spread);
    }
    private static double Correlation(short[] a, short[] b, int start, int count, int lag, int step)
    {
        if (start + lag < 0 || start + count + lag > b.Length) return -1;
        double sumA = 0, sumB = 0, squareA = 0, squareB = 0, product = 0; var n = 0;
        for (var i = 0; i < count; i += step) {
            var x = a[start + i]; var y = b[start + i + lag]; sumA += x; sumB += y;
            squareA += x * (double)x; squareB += y * (double)y; product += x * (double)y; n++;
        }
        var energyA = squareA - sumA * sumA / n; var energyB = squareB - sumB * sumB / n;
        return energyA < n * 16 || energyB < n * 16 ? -1 : (product - sumA * sumB / n) / Math.Sqrt(energyA * energyB);
    }
}
