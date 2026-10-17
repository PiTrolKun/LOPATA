using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.LyricVideo;

internal static class VideoExport
{
    public static async Task<string> RunAsync(ClipSession session, string destination, ClipOptions options, string operationId, double from, double length,
        Action<string> log, Action<double>? progress, CancellationToken token)
    {
        options.Validate();
        if (!Guid.TryParseExact(operationId, "N", out _)) throw new InvalidDataException("Invalid export identity.");
        if (from < 0 || length <= 0 || from + length > session.TotalDuration + .001) throw new ArgumentOutOfRangeException(nameof(length));
        if (File.Exists(destination) || Path.GetFullPath(destination).Equals(session.Config.Song, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must be a new file.");
        await session.VerifySourceAsync(token);
        var failures = new List<string>();
        foreach (var encoder in new[] { "hardware", "software", "openh264" })
        {
            try
            {
                await EncodeAsync(session, destination, options, operationId, from, length, encoder, log, progress, token);
                await session.VerifySourceAsync(token); return destination;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException error) { failures.Add(encoder + ": " + error.Message); log("Encoder fallback: " + failures[^1]); }
        }
        throw new IOException(string.Join("\n", failures));
    }
    private static async Task EncodeAsync(ClipSession session, string destination, ClipOptions options, string operationId, double from, double length, string encoder,
        Action<string> log, Action<double>? progress, CancellationToken token)
    {
        var temporary = destination + ".lopata-" + operationId + ".partial";
        // This private staging name is bound to the durable request, never to a finished user result.
        if (File.Exists(temporary)) File.Delete(temporary);
        string N(double number) => number.ToString("0.########", CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-n", "-filter_threads", "1",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1920x1080", "-framerate", "30", "-i", "pipe:0",
            "-f", "s16le", "-ar", "48000", "-ac", "2", "-ss", N(from), "-i", session.PcmPath,
            "-map", "0:v:0", "-map", "1:a:0", "-pix_fmt", encoder == "hardware" ? "nv12" : "yuv420p", "-threads", "2" };
        if (encoder == "openh264") args.AddRange(["-c:v", "libopenh264", "-b:v", "10000000"]);
        else args.AddRange(["-c:v", "h264_mf", "-hw_encoding", encoder == "hardware" ? "1" : "0",
            "-b:v", "10000000", "-rate_control", "quality", "-quality", "85"]);
        var tags = ClipMetadata.Create(session, operationId, options);
        var tagDirectory = Path.Combine(session.Config.Output, "metadata-" + operationId);
        args.AddRange(["-vf", $"scale={options.Width}:{options.Height}", "-af", "apad", "-c:a", "aac", "-b:a", "192k", "-t", N(length)]);
        args.AddRange(await ClipMetadata.ArgumentsAsync(tags, tagDirectory, token));
        args.AddRange(["-movflags", "+faststart+use_metadata_tags", "-f", "mp4", temporary]);
        var info = ClipRuntime.Info(Path.Combine(session.Config.VideoDirectory, "ffmpeg.exe"), args);
        info.WorkingDirectory = tagDirectory;
        info.RedirectStandardInput = true;
        using var process = new Process { StartInfo = info };
        try
        {
            if (!OwnedProcessRegistry.Shared.Start(process, "Music.Video.Encode")) throw new IOException("Encoder did not start.");
            log($"Export: {encoder}; own PID={process.Id}; from={from:F3}s length={length:F3}s; 1920x1080/30.");
            using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = ReadTailAsync(process.StandardError); var stdout = process.StandardOutput.ReadToEndAsync();
            try
            {
                using var renderer = new SceneRenderer(session, options);
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
                await VerifyAsync(temporary, options, length, tags, token);
                await session.VerifySourceAsync(token);
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination);
                log($"Export verified: {encoder}; {count / watch.Elapsed.TotalSeconds:F1} frames/s; {new FileInfo(destination).Length} bytes.");
                progress?.Invoke(1);
            }
            catch (Exception failure)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                var error = await errors; await stdout;
                token.ThrowIfCancellationRequested();
                if (error.Length > 0) throw new IOException(failure.Message + "\n" + error, failure); throw;
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(tagDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(tagDirectory, "*.txt")) File.Delete(file);
                Directory.Delete(tagDirectory);
            }
        }
    }
    internal static async Task VerifyAsync(string path, ClipOptions options, double duration, IReadOnlyDictionary<string, string>? tags, CancellationToken token)
    {
        var json = await ClipRuntime.RunAsync(["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token);
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(v => v.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(v => v.GetProperty("codec_type").GetString() == "audio");
        if (video.GetProperty("codec_name").GetString() != "h264" || video.GetProperty("width").GetInt32() != options.Width
            || video.GetProperty("height").GetInt32() != options.Height || video.GetProperty("avg_frame_rate").GetString() != "30/1"
            || audio.GetProperty("codec_name").GetString() != "aac") throw new IOException("Unexpected encoded streams.");
        var format = root.GetProperty("format");
        if (Math.Abs(double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - duration) > .1
            ) throw new IOException("Export duration verification failed.");
        if (tags is not null)
        {
            var actual = format.GetProperty("tags").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in tags)
                if (!actual.TryGetValue(pair.Key, out var value) || (pair.Key == "creation_time"
                    ? !DateTimeOffset.TryParse(value, out var stamp) || Math.Abs((stamp - DateTimeOffset.Parse(pair.Value)).TotalSeconds) > .01
                    : value != pair.Value)) throw new IOException("Inherited metadata round-trip failed: " + pair.Key);
        }
    }
    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var lines = new Queue<string>();
        while (await reader.ReadLineAsync() is { } line) { if (lines.Count == 15) lines.Dequeue(); lines.Enqueue(line); }
        return string.Join("\n", lines);
    }
}
