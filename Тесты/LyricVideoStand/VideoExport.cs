using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LyricVideoStand;

internal static class VideoExport
{
    public static async Task<string> RunAsync(StandSession session, string destination, double from, double length,
        StandLog log, Action<double>? progress, CancellationToken token)
    {
        if (from < 0 || length <= 0 || from + length > session.TotalDuration + .001) throw new ArgumentOutOfRangeException(nameof(length));
        if (File.Exists(destination) || Path.GetFullPath(destination).Equals(session.Config.Song, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must be a new file.");
        await session.VerifySourceAsync(token);
        var failures = new List<string>();
        foreach (var encoder in new[] { "hardware", "software", "openh264" })
        {
            try
            {
                await EncodeAsync(session, destination, from, length, encoder, log, progress, token);
                await session.VerifySourceAsync(token); return destination;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException error) { failures.Add(encoder + ": " + error.Message); log.Write("Encoder fallback: " + failures[^1]); }
        }
        throw new IOException(string.Join("\n", failures));
    }
    private static async Task EncodeAsync(StandSession session, string destination, double from, double length, string encoder,
        StandLog log, Action<double>? progress, CancellationToken token)
    {
        var temporary = destination + ".partial";
        if (File.Exists(temporary)) throw new IOException("Partial file already exists.");
        string N(double number) => number.ToString("0.########", CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-n", "-filter_threads", "1",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1920x1080", "-framerate", "30", "-i", "pipe:0",
            "-f", "s16le", "-ar", "48000", "-ac", "2", "-ss", N(from), "-i", session.PcmPath,
            "-map", "0:v:0", "-map", "1:a:0", "-pix_fmt", encoder == "hardware" ? "nv12" : "yuv420p", "-threads", "2" };
        if (encoder == "openh264") args.AddRange(["-c:v", "libopenh264", "-b:v", "10000000"]);
        else args.AddRange(["-c:v", "h264_mf", "-hw_encoding", encoder == "hardware" ? "1" : "0",
            "-b:v", "10000000", "-rate_control", "quality", "-quality", "85"]);
        var facts = session.Provenance with { Scenario = "lyric-video/stand-export" };
        args.AddRange(["-af", "apad", "-c:a", "aac", "-b:a", "192k", "-t", N(length),
            "-metadata", "title=" + session.Title, "-metadata", "artist=" + session.Artist,
            "-metadata", "comment=" + facts.Json(), "-metadata", "encoded_by=LOPATA " + session.Config.Version,
            "-metadata", "creation_time=" + facts.CreatedUtc.ToString("O"), "-movflags", "+faststart", "-f", "mp4", temporary]);
        var info = StandProcess.Info(Path.Combine(session.Config.VideoDirectory, "ffmpeg.exe"), args);
        info.RedirectStandardInput = true;
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("Encoder did not start.");
            log.Write($"Export: {encoder}; own PID={process.Id}; from={from:F3}s length={length:F3}s; 1920x1080/30.");
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = ReadTailAsync(process.StandardError); var stdout = process.StandardOutput.ReadToEndAsync();
            try
            {
                using var renderer = new SceneRenderer(session);
                var count = (long)Math.Ceiling(length * 30); var watch = Stopwatch.StartNew();
                for (long i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var pixels = renderer.Render(from + i / 30d);
                    using var stalled = CancellationTokenSource.CreateLinkedTokenSource(token); stalled.CancelAfter(TimeSpan.FromSeconds(30));
                    try { await process.StandardInput.BaseStream.WriteAsync(pixels, stalled.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Encoder stopped consuming frames."); }
                    if (i % 30 == 0) progress?.Invoke(i / (double)count);
                }
                process.StandardInput.Close();
                using var finish = CancellationTokenSource.CreateLinkedTokenSource(token); finish.CancelAfter(TimeSpan.FromMinutes(2));
                await process.WaitForExitAsync(finish.Token); var error = await errors; await stdout;
                if (process.ExitCode != 0) throw new IOException(error.Length == 0 ? "Encoder failed." : error);
                await VerifyAsync(session, temporary, length, facts.Json(), token);
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
                log.Write($"Export verified: {encoder}; {count / watch.Elapsed.TotalSeconds:F1} frames/s; {new FileInfo(destination).Length} bytes.");
                progress?.Invoke(1);
            }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                var error = await errors; await stdout;
                token.ThrowIfCancellationRequested();
                if (error.Length > 0) throw new IOException(error); throw;
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task VerifyAsync(StandSession session, string path, double duration, string facts, CancellationToken token)
    {
        var json = await StandProcess.RunAsync(Path.Combine(session.Config.VideoDirectory, "ffprobe.exe"),
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token);
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(v => v.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(v => v.GetProperty("codec_type").GetString() == "audio");
        if (video.GetProperty("codec_name").GetString() != "h264" || video.GetProperty("width").GetInt32() != 1920
            || video.GetProperty("height").GetInt32() != 1080 || video.GetProperty("avg_frame_rate").GetString() != "30/1"
            || audio.GetProperty("codec_name").GetString() != "aac") throw new IOException("Unexpected encoded streams.");
        var format = root.GetProperty("format");
        if (Math.Abs(double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - duration) > .1
            || format.GetProperty("tags").GetProperty("comment").GetString() != facts) throw new IOException("Export duration/provenance verification failed.");
    }
    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var lines = new Queue<string>();
        while (await reader.ReadLineAsync() is { } line) { if (lines.Count == 15) lines.Dequeue(); lines.Enqueue(line); }
        return string.Join("\n", lines);
    }
}
