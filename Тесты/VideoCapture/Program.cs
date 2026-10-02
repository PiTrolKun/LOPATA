using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AIHub.Models;
using AIHub.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Application = System.Windows.Application;
using Window = System.Windows.Window;
using Brushes = System.Windows.Media.Brushes;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Results = [];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; Results.Add(message); }
    [STAThread] private static int Main(string[] args)
    {
        if (args.Contains("--crash-worker"))
        {
            var directory = args[Array.IndexOf(args, "--crash-worker") + 1];
            var store = new VideoFrameStore(directory, new() { VideoFormat = "mp4", VideoFps = 15, Processing = "cpu" });
            store.Append(Pixels(320, 180, 40), 0); store.Append(Pixels(320, 180, 90), 1000); store.Append(Pixels(320, 180, 150), 2000);
            File.AppendAllText(Path.Combine(directory, "frames.jsonl"), "{last-uncommitted");
            File.WriteAllText(Path.Combine(directory, "ready"), "ready"); Thread.Sleep(Timeout.Infinite); return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 1;
        var run = Path.GetFullPath("Тесты/VideoCapture/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(run);
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { if (args.Contains("--quality")) await VideoQualityProbe.Run(run, args.Contains("--baseline")); else if (args.Contains("--import")) await VideoImportLoadProbe.Run(run, args.Contains("--maximum")); else if (args.Contains("--long")) await Long(run); else await Run(run, !args.Contains("--codecs")); Console.WriteLine($"PASS {_checks}: {run}"); result = 0; }
            catch (Exception error) { Console.WriteLine(error); File.WriteAllText(Path.Combine(run, "failure.txt"), error.ToString()); }
            finally { File.WriteAllLines(Path.Combine(run, "checks.txt"), Results); app.Shutdown(); }
        });
        app.Run(); return result;
    }
    public static async Task<JsonDocument> Probe(string file)
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(VideoEncoder.Executable)!, "ffprobe.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", file }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var text = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(error); return JsonDocument.Parse(text);
    }
    private static GifPixels Pixels(int width, int height, byte red)
    {
        var buffer = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { int i = (y * width + x) * 4; buffer[i] = (byte)(x % 255); buffer[i + 1] = (byte)(y % 255); buffer[i + 2] = red; buffer[i + 3] = 255; }
        return new(width, height, buffer);
    }
    private static async Task Run(string run, bool native = true)
    {
        foreach (var quality in new[] { "original", "720p", "1080p", "1440p", "4K" })
        {
            var size = VideoFrameStore.OutputSize(3840, 2160, quality);
            var expected = quality switch { "720p" => 720, "1080p" => 1080, "1440p" => 1440, _ => 2160 };
            Check(size.Height == expected && size.Width * 9 == size.Height * 16, "Preset " + quality + " preserves 16:9 physical geometry");
            size = VideoFrameStore.OutputSize(2160, 3840, quality);
            Check(size.Width == expected && size.Height * 9 == size.Width * 16, "Preset " + quality + " preserves portrait geometry");
        }
        Check(VideoFrameStore.OutputSize(641, 361, "4K") == (640, 360), "Odd small source is rounded to even dimensions without enlargement");
        foreach (var format in new[] { "mp4", "mkv", "webm" }) foreach (var mode in new[] { "cpu", "gpu" })
        {
            var settings = new ScreenCaptureSettings { Folder = run, VideoFormat = format, VideoFps = 15, VideoQuality = "original", Processing = mode };
            var store = new VideoFrameStore(Path.Combine(run, format + "-" + mode), settings);
            store.Append(Pixels(320, 180, 50), 0); store.Append(Pixels(320, 180, 50), 100); store.Append(Pixels(160, 90, 200), 900);
            store.Manifest.DurationMilliseconds = 2000; store.Manifest.ReducedDetail = true; store.Save();
            Check(Directory.GetFiles(store.DirectoryPath, "*.jpg").Length == 2, "Static frames share one cache file: " + format + mode);
            var file = Path.Combine(run, format + "-" + mode + "." + format);
            var encoded = await VideoEncoder.AssembleAsync(store, file, CancellationToken.None);
            using var probe = await Probe(file); var video = probe.RootElement.GetProperty("streams")[0];
            Check(video.GetProperty("width").GetInt32() == 320 && video.GetProperty("height").GetInt32() == 180, "Adaptive frames return to one output size: " + format + mode);
            Check(Math.Abs(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture) - 2) < 0.08, "Timestamps preserve two seconds including missing frames: " + format + mode);
            Check(video.GetProperty("codec_name").GetString() == (format == "webm" ? "vp9" : "h264"), "Correct format/codec: " + format + mode + "/" + encoded.Encoder);
            if (mode == "gpu" && format != "webm") Check(encoded.Encoder is "nvenc" or "hardware", "Real hardware encoding succeeded: " + format);
        }
        var recovery = new VideoFrameStore(Path.Combine(run, "recovery"), new() { VideoFormat = "mkv", VideoFps = 15, Processing = "cpu" });
        recovery.Append(Pixels(320, 180, 40), 0); recovery.Append(Pixels(320, 180, 40), 1800); recovery.Save();
        File.AppendAllText(Path.Combine(recovery.DirectoryPath, "frames.jsonl"), "{interrupted");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); bool cancelled = false;
            try { await VideoEncoder.AssembleAsync(recovery, Path.Combine(run, "cancelled.mkv"), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && File.Exists(Path.Combine(recovery.DirectoryPath, "0000000000.jpg")), "Cancelling finalization preserves the capture cache");
        }
        var recovered = await VideoRecordingSession.RecoverAsync(recovery.DirectoryPath, CancellationToken.None);
        using (var probe = await Probe(recovered)) Check(probe.RootElement.GetProperty("streams").GetArrayLength() == 1, "Interrupted journal can be assembled again");
        File.WriteAllText(Path.Combine(recovery.DirectoryPath, "keep.txt"), "unrelated"); recovery.Cleanup();
        Check(File.Exists(Path.Combine(recovery.DirectoryPath, "keep.txt")) && !File.Exists(Path.Combine(recovery.DirectoryPath, "0000000000.jpg")), "Cleanup removes shared frame once and preserves unrelated files");
        await AudioMix(run);
        await Crash(run);
        await VideoQualityProbe.Adaptation(run);
        if (!native) return;
        var window = new Window { Width = 520, Height = 320, Content = new TextBlock { Text = "LOPATA video timing probe", FontSize = 28, Background = Brushes.DarkBlue, Foreground = Brushes.White } };
        window.Show(); await Task.Delay(180);
        try
        {
            foreach (var processing in new[] { "cpu", "gpu" })
            {
                var session = new VideoRecordingSession(new() { Folder = run, Processing = processing, VideoFps = 15, VideoQuality = "720p" }, CaptureSource.Window);
                var task = session.RunAsync(new WindowInteropHelper(window).Handle, true, default, CancellationToken.None);
                await Task.Delay(2200); session.Stop(); var result = await task;
                Check(result.File is not null && result.Frames > 3, "Native window capture " + processing + ": " + result.Error);
            }
            var display = CaptureDisplayGeometry.AtCursor();
            foreach (var source in new[] { CaptureSource.Monitor, CaptureSource.Desktop, CaptureSource.Area })
            {
                var bounds = source == CaptureSource.Desktop ? CaptureDisplayGeometry.Union(CaptureDisplayGeometry.Displays()) : source == CaptureSource.Area ? new Int32Rect(display.Bounds.X, display.Bounds.Y, 640, 360) : display.Bounds;
                var session = new VideoRecordingSession(new() { Folder = run, VideoFps = 15, VideoQuality = "720p", Processing = "cpu" }, source);
                var task = session.RunAsync(display.Handle, false, bounds, CancellationToken.None);
                await Task.Delay(1600); session.Stop(); var result = await task;
                Check(result.File is not null && result.Frames > 3, "Native " + source + ": " + result.Error);
            }
            foreach (var audioMode in new[] { "off", "pc", "mic", "both" })
            {
                var session = new VideoRecordingSession(new() { Folder = run, VideoFps = 15, Processing = "cpu", AudioMode = audioMode }, CaptureSource.Area);
                var bounds = new Int32Rect(display.Bounds.X, display.Bounds.Y, 320, 180);
                var task = session.RunAsync(0, false, bounds, CancellationToken.None);
                await Task.Delay(1600); session.Stop(); var result = await task;
                Check(result.File is not null && result.Error.Length == 0, "Native audio " + audioMode + ": " + result.Error);
                using var probe = await Probe(result.File!);
                Check(probe.RootElement.GetProperty("streams").GetArrayLength() == (audioMode == "off" ? 1 : 2), "Requested audio track only: " + audioMode);
            }
            Check(VideoAudioCapture.Devices(false).Count > 0 && VideoAudioCapture.Devices(true).Count > 0, "Real playback and microphone choices are available");
        }
        finally { window.Close(); }
    }
    private static async Task Long(string run)
    {
        var display = CaptureDisplayGeometry.AtCursor();
        var session = new VideoRecordingSession(new() { Folder = run, VideoFps = 15, VideoQuality = "original", Processing = "cpu", AudioMode = "pc" }, CaptureSource.Area);
        var task = session.RunAsync(0, false, new(display.Bounds.X, display.Bounds.Y, 320, 180), CancellationToken.None);
        var rss = new List<long>();
        for (var i = 0; i < 65; i++) { await Task.Delay(1000); rss.Add(Process.GetCurrentProcess().WorkingSet64); if (i % 10 == 0) Console.WriteLine("LONG " + i); }
        Check(!task.IsCompleted, "Video recording continues beyond the GIF one-minute limit"); session.Stop(); var result = await task;
        Check(result.File is not null && result.Error.Length == 0, "65-second video completes: " + result.Error);
        using var probe = await Probe(result.File!); var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Check(duration >= 65 && duration <= 68, "Video/audio retain the long recording duration");
        File.WriteAllText(Path.Combine(run, "long.json"), JsonSerializer.Serialize(new { result, duration, rss }));
    }
    private static async Task<byte[]> Decode(string file, params string[] options)
    {
        var info = new ProcessStartInfo(VideoEncoder.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", file }.Concat(options)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; using var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output); var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); await reading;
        if (process.ExitCode != 0) throw new Exception(await errors); return output.ToArray();
    }
    private static async Task AudioMix(string run)
    {
        var store = new VideoFrameStore(Path.Combine(run, "audio-mix"), new() { VideoFormat = "mp4", VideoFps = 15, Processing = "cpu" });
        store.Append(Pixels(320, 180, 40), 0); store.Append(Pixels(320, 180, 200), 1000); store.Manifest.DurationMilliseconds = 2000;
        foreach (var name in new[] { "playback.pcm", "microphone.pcm" })
        {
            using var file = File.Create(Path.Combine(store.DirectoryPath, name)); using var writer = new BinaryWriter(file);
            for (var i = 0; i < 96000; i++) { float value = i is >= 24000 and < 48000 ? (float)(0.1 * Math.Sin(i * 2 * Math.PI * 440 / 48000)) : 0; writer.Write(value); writer.Write(value); }
            store.Manifest.Audio.Add(new(name, "f32le", 48000, 2, 8));
        }
        store.Save(); var fileName = Path.Combine(run, "audio-mix.mp4"); await VideoEncoder.AssembleAsync(store, fileName, CancellationToken.None);
        var decoded = await Decode(fileName, "-vn", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1");
        double Rms(int begin, int end) { double sum = 0; for (var i = begin; i < end; i++) { var value = BitConverter.ToSingle(decoded, i * 8); sum += value * value; } return Math.Sqrt(sum / (end - begin)); }
        Check(Rms(30000, 40000) > 0.13 && Rms(30000, 40000) < 0.17, "PC and microphone are mixed without erasing half their amplitude");
        Check(Rms(2000, 12000) < 0.001 && Rms(65000, 85000) < 0.001, "Audio silence/timing preserved before and after the half-second tone");
        var before = await Decode(fileName, "-ss", "0.5", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
        var after = await Decode(fileName, "-ss", "1.5", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
        Check(after[0] > before[0] + 80, "Video content changes at the journal boundary rather than merely producing a valid container");
    }
    private static async Task Crash(string run)
    {
        var folder = Path.Combine(run, "abrupt-process");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--crash-worker"); info.ArgumentList.Add(folder);
        using var worker = Process.Start(info)!;
        try
        {
            var limit = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(folder, "ready")) && limit.Elapsed.TotalSeconds < 20) await Task.Delay(50);
            Check(File.Exists(Path.Combine(folder, "ready")), "Owned crash worker commits video frames before termination");
            worker.Kill(); await worker.WaitForExitAsync();
            var output = await VideoRecordingSession.RecoverAsync(folder, CancellationToken.None);
            using var probe = await Probe(output); var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Check(duration > 2 && duration < 2.2, "Abruptly killed process recovers committed frames and timing");
        }
        finally { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
    }
}
