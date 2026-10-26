using System.IO;
using System.Text;

namespace AIHub.Services.VideoEditor;

internal sealed class VideoWaveform
{
    private const int Rate = 48000, MaxBins = 2_000_000, Signature = 0x3257564c;
    private readonly float[][] _levels;
    internal double BinSeconds { get; }
    internal int Count => _levels[0].Length;
    internal static VideoWaveform Empty { get; } = new(192, []);
    internal VideoWaveform(int window, float[] peaks)
    {
        BinSeconds = window / (double)Rate; var levels = new List<float[]> { peaks };
        while (peaks.Length > 1) { var next = new float[(peaks.Length + 1) / 2]; for (var i = 0; i < next.Length; i++) next[i] = Math.Max(peaks[i * 2], i * 2 + 1 < peaks.Length ? peaks[i * 2 + 1] : 0); levels.Add(next); peaks = next; }
        _levels = levels.ToArray();
    }
    internal double Peak(double from, double to)
    {
        if (Count == 0 || to <= 0 || from >= Count * BinSeconds) return 0;
        var left = (int)Math.Clamp(Math.Floor(from / BinSeconds), 0, Count - 1); var right = (int)Math.Clamp(Math.Ceiling(to / BinSeconds), left + 1, Count);
        float peak = 0; var level = 0;
        while (left < right) { if ((left & 1) != 0) peak = Math.Max(peak, _levels[level][left++]); if ((right & 1) != 0) peak = Math.Max(peak, _levels[level][--right]); left /= 2; right /= 2; level++; }
        return peak;
    }
    internal static string CachePath(VideoAsset a, string work) => Path.Combine(work, a.Id + ".wave-peaks-v2.bin");
    internal static Task<VideoWaveform> LoadAsync(VideoAsset a, string work, CancellationToken token) => Task.Run(async () => {
        token.ThrowIfCancellationRequested(); if (a.AudioStream < 0) return Empty;
        Directory.CreateDirectory(work); var path = CachePath(a, work);
        if (File.Exists(path)) {
            try {
                using var reader = new BinaryReader(File.OpenRead(path));
                if (reader.ReadInt32() != Signature || reader.ReadInt64() != a.Length || reader.ReadInt64() != a.LastWriteUtc.Ticks || reader.ReadInt32() != a.AudioStream) throw new InvalidDataException();
                var window = reader.ReadInt32(); var count = reader.ReadInt32();
                if (window < 192 || count < 0 || count > MaxBins || reader.BaseStream.Length - reader.BaseStream.Position != count * 4L) throw new InvalidDataException();
                var peaks = new float[count]; for (var i = 0; i < count; i++) { peaks[i] = reader.ReadSingle(); if (!float.IsFinite(peaks[i]) || peaks[i] is < 0 or > 1) throw new InvalidDataException(); }
                token.ThrowIfCancellationRequested(); return new VideoWaveform(window, peaks);
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { OwnedProcessRegistry.Log("video_waveform_cache_rebuild", "VideoEditor", detail: error.GetType().Name); }
        }
        var binFrames = (int)Math.Clamp(Math.Ceiling(a.Duration * Rate / MaxBins), 192, int.MaxValue / 2);
        var bins = new List<float>(); var inBin = 0; float maximum = 0;
        using var process = OwnedProcessRegistry.Shared.Start(VideoRuntime.Info(false, ["-v", "error", "-nostdin", "-i", a.Path, "-map", "0:" + a.AudioStream, "-vn", "-ar", Rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", "2", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]), "VideoEditor.Waveform");
        var errors = TailAsync(process.StandardError);
        void Stop() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        using var cancel = token.Register(Stop);
        try {
            var buffer = new byte[65540]; var carry = 0; int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry, 65536), token).ConfigureAwait(false)) > 0) {
                var available = carry + read; var complete = available - available % 4;
                for (var b = 0; b < complete; b += 4) {
                    var peak = Math.Max(Math.Abs((int)BitConverter.ToInt16(buffer, b)), Math.Abs((int)BitConverter.ToInt16(buffer, b + 2))) / 32768f;
                    maximum = Math.Max(maximum, peak);
                    if (++inBin < binFrames) continue;
                    bins.Add(maximum); maximum = 0; inBin = 0;
                    if (bins.Count == MaxBins) { for (var i = 0; i < MaxBins / 2; i++) bins[i] = Math.Max(bins[2 * i], bins[2 * i + 1]); bins.RemoveRange(MaxBins / 2, MaxBins / 2); binFrames = checked(binFrames * 2); }
                }
                carry = available - complete; if (carry > 0) Buffer.BlockCopy(buffer, complete, buffer, 0, carry);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); var stderr = await errors.ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("VideoEditor.RuntimeFailed: " + stderr);
            if (inBin > 0) bins.Add(maximum);
        }
        catch { Stop(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); await errors.ConfigureAwait(false); throw; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var writer = new BinaryWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))) { writer.Write(Signature); writer.Write(a.Length); writer.Write(a.LastWriteUtc.Ticks); writer.Write(a.AudioStream); writer.Write(binFrames); writer.Write(bins.Count); foreach (var peak in bins) writer.Write(peak); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new VideoWaveform(binFrames, bins.ToArray());
    }, token);
    private static async Task<string> TailAsync(StreamReader reader)
    {
        var result = new StringBuilder(); while (await reader.ReadLineAsync().ConfigureAwait(false) is {} line) { result.AppendLine(line); if (result.Length > 65536) result.Remove(0, result.Length / 2); } return result.ToString();
    }
}
