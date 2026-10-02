using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AIHub.Services;

public sealed record VideoEncodingResult(string Path, string Encoder, string Fallback);

/// <summary>Only finalization uses the bundled encoder. Capture never waits for it.
/// Timestamped frames are replayed on a fixed clock; missed frames hold the preceding picture.</summary>
public static class VideoEncoder
{
    public static string Executable => Path.Combine(AppContext.BaseDirectory, "CaptureRuntime", "ffmpeg.exe");
    public static async Task<VideoEncodingResult> AssembleAsync(VideoFrameStore store, string destination, CancellationToken token, Action<int>? progress = null)
    {
        if (!File.Exists(Executable)) throw new IOException("The bundled video encoder is missing. Repair the LOPATA installation.");
        var settings = store.Manifest.Settings;
        var choices = settings.VideoFormat == "webm" ? new[] { "vp9" } : settings.Processing == "gpu" ? new[] { "nvenc", "hardware", "software", "openh264" } : new[] { "software", "openh264" };
        var errors = new List<string>();
        foreach (var encoder in choices)
        {
            try { await EncodeAsync(store, destination, encoder, token, progress); return new(destination, encoder, string.Join("\n", errors)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { errors.Add(encoder + ": " + error.Message); }
        }
        throw new IOException(string.Join("\n", errors));
    }
    private static async Task EncodeAsync(VideoFrameStore store, string destination, string encoder, CancellationToken token, Action<int>? progress)
    {
        var manifest = store.Manifest; var settings = manifest.Settings;
        var temporary = destination + ".partial";
        if (File.Exists(destination)) throw new IOException("Video output already exists.");
        using var entries = store.Frames().GetEnumerator();
        if (!entries.MoveNext()) throw new IOException("No usable video frames were received.");
        var current = entries.Current; var hasNext = entries.MoveNext();
        // Recover journals interrupted before their final duration update.
        var duration = Math.Max(1d / settings.VideoFps, manifest.DurationMilliseconds / 1000);
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
        void Args(params string[] args) { foreach (var arg in args) info.ArgumentList.Add(arg); }
        string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
        Args("-hide_banner", "-loglevel", "warning", "-nostats", "-y", "-filter_threads", "1", "-filter_complex_threads", "1",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{manifest.Width}x{manifest.Height}", "-framerate", settings.VideoFps.ToString(), "-i", "pipe:0");
        foreach (var audio in manifest.Audio)
        {
            VideoFrameStore.SafeName(audio.Name, ".pcm"); var file = Path.Combine(store.DirectoryPath, audio.Name);
            if (!File.Exists(file)) throw new IOException("A cached audio track is missing.");
            if (new FileInfo(file).Length == 0) Args("-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo");
            else Args("-f", audio.Format, "-ar", audio.Rate.ToString(), "-ac", audio.Channels.ToString(), "-i", file);
        }
        Args("-map", "0:v:0", "-pix_fmt", encoder == "hardware" ? "nv12" : "yuv420p", "-threads", "2");
        var bitrate = Math.Clamp(manifest.Width * (double)manifest.Height * settings.VideoFps * (0.025 + settings.VideoCompression * 0.0015), 200_000, 100_000_000);
        if (encoder == "vp9") Args("-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-crf", Number(55 - settings.VideoCompression * 0.4), "-b:v", "0");
        else if (encoder == "nvenc") Args("-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", Number(51 - settings.VideoCompression * 0.45), "-b:v", Number(bitrate));
        else if (encoder == "openh264") Args("-c:v", "libopenh264", "-b:v", Number(bitrate));
        else Args("-c:v", "h264_mf", "-hw_encoding", encoder == "hardware" ? "1" : "0", "-b:v", Number(bitrate), "-rate_control", "quality", "-quality", settings.VideoCompression.ToString());
        if (manifest.Audio.Count > 0)
        {
            var filters = string.Join(';', manifest.Audio.Select((_, i) => $"[{i + 1}:a]aresample=48000,aformat=channel_layouts=stereo,apad,atrim=duration={Number(duration)}[a{i}]"));
            if (manifest.Audio.Count == 1) filters += ";[a0]anull[mixed]";
            else filters += ";[a0][a1]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:latency=1[mixed]";
            Args("-filter_complex", filters, "-map", "[mixed]", "-c:a", settings.VideoFormat == "webm" ? "libopus" : "aac", "-b:a", "192k");
        }
        else Args("-an");
        Args("-t", Number(duration));
        if (settings.VideoFormat == "mp4") Args("-movflags", "+faststart");
        Args("-f", settings.VideoFormat == "mkv" ? "matroska" : settings.VideoFormat, temporary);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("The video encoder could not be started.");
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (System.ComponentModel.Win32Exception) { }
            var errorText = ReadErrorsAsync(process.StandardError);
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            string? loadedName = null; GifPixels? pixels = null;
            var total = (long)Math.Ceiling(duration * settings.VideoFps); var lastPercent = -1;
            try
            {
                for (long i = 0; i < total; i++)
                {
                    token.ThrowIfCancellationRequested(); var at = i * 1000d / settings.VideoFps;
                    while (hasNext && entries.Current.At <= at) { current = entries.Current; hasNext = entries.MoveNext(); }
                    if (loadedName != current.Name)
                    { pixels = GifPixels.Resize(store.Read(current), manifest.Width, manifest.Height); loadedName = current.Name; }
                    using var stalled = CancellationTokenSource.CreateLinkedTokenSource(token); stalled.CancelAfter(TimeSpan.FromSeconds(30));
                    try { await process.StandardInput.BaseStream.WriteAsync(pixels!.Bgra, stalled.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The video encoder stopped consuming frames for 30 seconds."); }
                    var percent = (int)((i + 1) * 99 / total); if (percent != lastPercent) { progress?.Invoke(percent); lastPercent = percent; }
                }
                process.StandardInput.Close();
                using var finishing = CancellationTokenSource.CreateLinkedTokenSource(token); finishing.CancelAfter(TimeSpan.FromMinutes(2));
                await process.WaitForExitAsync(finishing.Token);
                var errors = await errorText;
                if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length < 100) throw new IOException(errors.Length == 0 ? "The video encoder did not produce a complete file." : errors);
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination); progress?.Invoke(100);
            }
            catch
            {
                if (!process.HasExited) process.Kill(); await process.WaitForExitAsync();
                var errors = await errorText;
                token.ThrowIfCancellationRequested();
                if (errors.Length > 0) throw new IOException(errors); throw;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var tail = new Queue<string>();
        while (await reader.ReadLineAsync() is { } line) { if (tail.Count == 20) tail.Dequeue(); tail.Enqueue(line); }
        return string.Join("\n", tail);
    }
}
