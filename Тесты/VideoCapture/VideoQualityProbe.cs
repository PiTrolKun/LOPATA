using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using AIHub.Models;
using AIHub.Services;

internal static class VideoQualityProbe
{
    public static async Task Adaptation(string run)
    {
        var pixels = new GifPixels(640, 360, Enumerable.Repeat((byte)128, 640 * 360 * 4).ToArray());
        foreach (var quality in new[] { 75, 100 })
        {
            var clock = Stopwatch.StartNew();
            var session = new VideoRecordingSession(new() { Folder = run, VideoFps = 15, VideoCompression = quality, Processing = "cpu" }, CaptureSource.Area);
            session.Progress += p => { if (p.Assembling) session.CancelAssembly(); };
            GifPixels Capture(Int32Rect _, int percent, bool cursor)
            {
                if (clock.Elapsed.TotalMilliseconds < 1700) Thread.Sleep(95);
                return GifPixels.Resize(pixels, 640 * percent / 100, 360 * percent / 100);
            }
            var task = session.RunAsync(0, false, new(0, 0, 640, 360), CancellationToken.None, Capture);
            await Task.Delay(8000); session.CancelAssembly(); var result = await task;
            var store = VideoFrameStore.Open(result.CacheDirectory); var frames = store.Frames().ToArray();
            var minimum = frames.Min(f => f.Width); var last = frames[^1].Width;
            if (quality == 100 ? minimum != 640 || result.ReducedDetail : minimum >= 640 || last != 640)
                throw new Exception("Pressure recovery/detail contract failed for " + quality);
            Console.WriteLine($"ADAPTATION {quality}: minimum {minimum}, final {last}, missed {result.MissedFrames}");
            store.Cleanup();
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public static async Task Run(string run, bool baseline)
    {
        // No overlay: exercise the user's ordinary desktop and foreground window.
        var handle = GetForegroundWindow();
        var display = CaptureDisplayGeometry.Displays().OrderByDescending(d => d.Bounds.Width).First();
        var results = new List<object>();
        foreach (var source in new[] { CaptureSource.Window, CaptureSource.Monitor, CaptureSource.Desktop, CaptureSource.Area })
        {
            var bounds = source == CaptureSource.Desktop ? CaptureDisplayGeometry.Union(CaptureDisplayGeometry.Displays()) :
                source == CaptureSource.Area ? new Int32Rect(display.Bounds.X + 100, display.Bounds.Y + 100, 1280, 720) : display.Bounds;
            var settings = new ScreenCaptureSettings { Folder = run, VideoQuality = "4K", VideoCompression = 100, VideoFps = 60, VideoFormat = "mkv", Processing = "gpu", IncludeCursor = false };
            var session = new VideoRecordingSession(settings, source);
            session.Progress += p => { if (p.Assembling) session.CancelAssembly(); };
            var target = source == CaptureSource.Window ? handle : source == CaptureSource.Monitor ? display.Handle : 0;
            var task = session.RunAsync(target, source == CaptureSource.Window, bounds, CancellationToken.None);
            await Task.Delay(5000); session.CancelAssembly(); var recorded = await task;
            File.WriteAllText(Path.Combine(run, source + "-recording.json"), JsonSerializer.Serialize(recorded));
            var store = VideoFrameStore.Open(recorded.CacheDirectory); var entries = store.Frames().ToArray();
            var minWidth = entries.Min(e => e.Width); var minHeight = entries.Min(e => e.Height);
            var file = await VideoRecordingSession.RecoverAsync(recorded.CacheDirectory, CancellationToken.None);
            // Compare a decoded native-detail crop to the cached source at the same timestamp.
            var selected = entries.LastOrDefault(e => e.At <= 4000) ?? entries[0];
            var pixels = GifPixels.Resize(store.Read(selected), store.Manifest.Width, store.Manifest.Height);
            // Choose a crop with edges/text, rather than validating an empty desktop background.
            var x0 = 0; var y0 = 0; long best = -1;
            for (var cy = 0; cy <= pixels.Height - 64; cy += 64) for (var cx = 0; cx <= pixels.Width - 256; cx += 128)
            {
                long score = 0;
                for (var y = cy; y < cy + 64; y += 2) for (var x = cx + 1; x < cx + 256; x += 2)
                { var p = (y * pixels.Width + x) * 4; score += Math.Abs(pixels.Bgra[p] - pixels.Bgra[p - 4]) + Math.Abs(pixels.Bgra[p + 1] - pixels.Bgra[p - 3]) + Math.Abs(pixels.Bgra[p + 2] - pixels.Bgra[p - 2]); }
                if (score > best) { best = score; x0 = cx; y0 = cy; }
            }
            var info = new ProcessStartInfo(VideoEncoder.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-ss", "4", "-i", file, "-frames:v", "1", "-vf", $"crop=256:64:{x0}:{y0}", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; using var output = new MemoryStream();
            var stderr = process.StandardError.ReadToEndAsync(); await process.StandardOutput.BaseStream.CopyToAsync(output); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await stderr);
            var decoded = output.ToArray(); double squareError = 0;
            File.WriteAllBytes(Path.Combine(run, source + "-detail.bgra"), decoded);
            for (var y = 0; y < 64; y++) for (var x = 0; x < 256; x++) for (var c = 0; c < 3; c++)
            { var difference = decoded[(y * 256 + x) * 4 + c] - pixels.Bgra[((y0 + y) * pixels.Width + x0 + x) * 4 + c]; squareError += difference * difference; }
            var rmse = Math.Sqrt(squareError / (64 * 256 * 3));
            results.Add(new { source = source.ToString(), recorded, minWidth, minHeight, rmse, file });
            File.WriteAllText(Path.Combine(run, "quality.json"), JsonSerializer.Serialize(results));
            Console.WriteLine($"QUALITY {source}: cache {minWidth}x{minHeight}, output {store.Manifest.Width}x{store.Manifest.Height}, decode RMSE {rmse:F1}, frames {entries.Length}, missed {recorded.MissedFrames}");
            if (!baseline && (minWidth < store.Manifest.Width || minHeight < store.Manifest.Height || recorded.ReducedDetail || rmse > 30)) throw new Exception("Maximum quality lost native fine details: " + source);
        }
    }
}
