using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Controls;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Checks = [];
    [STAThread] private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var status = 1;
        var run = Path.GetFullPath("Тесты/GifCapture/runs/" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(run);
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                if (arguments.Contains("--crash-worker"))
                {
                    var cache = arguments[Array.IndexOf(arguments, "--crash-worker") + 1];
                    var durable = new GifFrameStore(cache, true);
                    for (var i = 0; i < 4; i++) durable.Append(Solid(80, 40, (byte)(i * 60)), i * 100);
                    File.WriteAllText(Path.Combine(cache, "recording.json.tmp"), "{unfinished");
                    File.WriteAllText(Path.Combine(cache, "ready"), "4 frames flushed");
                    await Task.Delay(Timeout.Infinite);
                }
                else if (arguments.Contains("--import")) await ImportLoadProbe.Run(run);
                else if (Environment.GetCommandLineArgs().Contains("--minute"))
                {
                    var minute = new GifRecordingSession(new() { Folder = run, GifSeconds = 60, GifFps = 15 }, CaptureSource.Area);
                    var result = await minute.RunAsync(0, false, new(0, 0, 120, 80), default);
                    var duration = Decode(result.File!).Frames.Sum(f => Convert.ToInt32(((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")));
                    Check(duration is >= 5980 and <= 6005 && result.Error.Length == 0, "Real sixty-second limit and 15FPS timing");
                }
                else await Run(run);
                status = 0; Console.WriteLine($"PASS {_checks}: {run}");
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(run, "failure.txt"), error.ToString()); Console.WriteLine(error); }
            finally { File.WriteAllLines(Path.Combine(run, "checks.txt"), Checks); app.Shutdown(); }
        }); app.Run(); return status;
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); Checks.Add(name); _checks++; }
    private static GifPixels Solid(int w, int h, byte red)
    { var bytes = new byte[w * h * 4]; for (var i = 0; i < bytes.Length; i += 4) { bytes[i + 2] = red; bytes[i + 3] = 255; } return new(w, h, bytes); }
    private static GifBitmapDecoder Decode(string path) => new(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    private static async Task Run(string run)
    {
        var random = new Random(983); var noise = new byte[600 * 400]; random.NextBytes(noise);
        var path = Path.Combine(run, "noise.gif");
        using (var file = File.Create(path)) { using var gif = new GifStreamEncoder(file, 600, 400, true); gif.Add(noise, 13); gif.Add(noise, 7); gif.Finish(); }
        var decoded = Decode(path); Check(decoded.Frames.Count == 2, "Independent WIC decoder accepts LZW dictionary growth and resets");
        foreach (var frame in decoded.Frames)
        {
            var pixels = ScreenshotFiles.Pixels(frame);
            var valid = true;
            for (var i = 0; i < noise.Length; i += 193)
            {
                var n = noise[i]; var r = n < 216 ? (n / 36) * 51 : (int)Math.Round((n - 216) * 255d / 39);
                valid &= pixels[i * 4 + 2] == r;
            }
            Check(valid, "Independent decoder restores palette indices across dictionary resets");
        }
        var store = new GifFrameStore(Path.Combine(run, "cache"), true);
        store.Append(Solid(120, 80, 255), 0); store.Append(Solid(120, 80, 255), 66.6667);
        store.Append(Solid(60, 40, 0), 133.3333); store.Append(Solid(60, 40, 0), 200);
        store.Manifest.DurationMilliseconds = 1000; store.Manifest.ReducedDetail = true; store.SaveManifest();
        var merged = Path.Combine(run, "merged.gif"); store.Assemble(merged, default); var frames = Decode(merged).Frames;
        Check(frames.Count == 2 && frames.All(f => f.PixelWidth == 120 && f.PixelHeight == 80), "Repeat frames merge and reduced cache frames normalize geometry");
        var delay = frames.Sum(f => Convert.ToInt32(((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")));
        Check(delay == 100, "GIF centisecond rounding preserves total duration");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); try { store.Assemble(Path.Combine(run, "cancelled.gif"), cancel.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Check(!File.Exists(Path.Combine(run, "cancelled.gif.tmp")) && File.Exists(Path.Combine(store.DirectoryPath, "recording.json")), "Cancelled assembly preserves cache without claiming output");
        }
        var recovered = await GifRecordingSession.RecoverAsync(store.DirectoryPath, default);
        Check(Decode(recovered).Frames.Count == 2, "Durable recording recovers after a fresh store instance");
        var crashCache = Path.Combine(run, "interrupted-process");
        using (var worker = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--crash-worker", crashCache } })!)
        {
            try
            {
                var journal = Path.Combine(crashCache, "recording.json");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(crashCache, "ready")) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
                Check(File.Exists(Path.Combine(crashCache, "ready")) && File.Exists(journal + ".tmp") && !worker.HasExited,
                    "Worker persisted frames before interrupted journal replacement");
                worker.Kill(); await worker.WaitForExitAsync();
                var interruptedGif = await GifRecordingSession.RecoverAsync(crashCache, default);
                Check(Decode(interruptedGif).Frames.Count == 4, "Recovery after abrupt worker termination ignores incomplete temporary journal");
            }
            finally { if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); } }
        }
        var slowRecording = new GifRecordingSession(new() { Folder = run, GifSeconds = 5, GifFps = 15 }, CaptureSource.Area);
        var slowTask = slowRecording.RunAsync(0, false, new(0, 0, 240, 160), default, (bounds, percent, _) =>
        { Thread.Sleep(160); return Solid(bounds.Width * percent / 100, bounds.Height * percent / 100, 255); });
        await Task.Delay(1100); slowRecording.Stop(); var slowResult = await slowTask;
        Check(slowResult.File is not null && slowResult.MissedFrames > 0 && slowResult.ReducedDetail,
            "A slow collector reports lost samples and lowers internal dimensions");
        Check(Decode(slowResult.File!).Frames[0].PixelWidth == 240, "Adaptive collection preserves chosen output dimensions");
        var deferred = new GifRecordingSession(new() { Folder = run, GifSeconds = 5 }, CaptureSource.Area);
        deferred.Progress += p => { if (p.Assembling) deferred.CancelAssembly(); };
        var deferredTask = deferred.RunAsync(0, false, new(0, 0, 80, 40), default, (bounds, percent, _) => Solid(bounds.Width, bounds.Height, 255));
        await Task.Delay(400); deferred.Stop(); var deferredResult = await deferredTask;
        Check(deferredResult.File is null && File.Exists(Path.Combine(deferredResult.CacheDirectory, "recording.json")), "Deferred session keeps durable input");
        Check(File.Exists(await GifRecordingSession.RecoverAsync(deferredResult.CacheDirectory, default)), "Deferred session can be finalized later");
        foreach (var percent in new[] { 100, 75, 50, 25 })
        {
            var frame = BitmapSource.Create(400, 200, 96, 96, PixelFormats.Pbgra32, null, Solid(400, 200, 255).Bgra, 1600); frame.Freeze();
            var output = GifPixels.From(frame, percent); Check(output.Width == 400 * percent / 100 && output.Height == 200 * percent / 100, "Output scale " + percent);
        }
        var bad = new ScreenCaptureSettings { GifScalePercent = 5 }; bad.Hotkeys["Gif.Desktop"] = [123]; bad.Normalize();
        Check(bad.GifScalePercent == 100 && !bad.Hotkeys.ContainsKey("Gif.Desktop") && ScreenCaptureSettings.Sources(CaptureMode.Gif).Count() == 3, "GIF source migration and scale normalization");
        var theme = new Window { Width = 600, Height = 380, Content = new TextBlock { Text = "LOPATA GIF probe", FontSize = 36, Background = Brushes.DarkBlue, Foreground = Brushes.White } };
        theme.Resources["WindowBackgroundBrush"] = Brushes.MidnightBlue; theme.Resources["TextPrimaryBrush"] = Brushes.White;
        theme.Resources["TextSecondaryBrush"] = Brushes.LightGray; theme.Resources["SecondaryButtonBackgroundBrush"] = Brushes.MidnightBlue;
        theme.Resources["LineBrush"] = Brushes.SlateGray; theme.Resources["AccentBrush"] = Brushes.RoyalBlue; theme.Resources["UiBodyFontSize"] = 16d;
        theme.Show(); await Task.Delay(200);
        try
        {
            var localization = new LocalizationService(); localization.Load("ru");
            using var stop = new CancellationTokenSource();
            var session = new GifRecordingSession(new() { Folder = run, GifSeconds = 5, GifScalePercent = 50, GifFps = 10, IncludeCursor = false }, CaptureSource.Window);
            GifRecordingProgress? last = null; var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Progress += p => { last = p; if (!p.Assembling) first.TrySetResult(); };
            var task = session.RunAsync(new WindowInteropHelper(theme).Handle, true, default, default);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(300);
            ((TextBlock)theme.Content).Background = Brushes.DarkRed; await Task.Delay(800); session.Stop();
            var result = await task; Check(result.File is not null && result.Error.Length == 0, "Persistent WARP window recording stops manually and finalizes");
            var gif = Decode(result.File!); Console.WriteLine($"WGC: {result.Frames} cached frames, {gif.Frames.Count} GIF frames, {gif.Frames[0].PixelWidth}x{gif.Frames[0].PixelHeight}");
            Check(gif.Frames.Count >= 2 && gif.Frames[0].PixelWidth == last!.Width, "Real WGC changed frames and scale captured");
            Check(!Directory.Exists(result.CacheDirectory) && last?.Assembling == true, "Cache cleanup follows output commit");
            var display = CaptureDisplayGeometry.AtCursor();
            var monitor = new GifRecordingSession(new() { Folder = run, GifSeconds = 5, GifScalePercent = 25, IncludeCursor = true }, CaptureSource.Monitor);
            var monitorTask = monitor.RunAsync(display.Handle, false, display.Bounds, default); await Task.Delay(750); monitor.Stop();
            var monitorResult = await monitorTask; Check(monitorResult.File is not null && monitorResult.Error.Length == 0, "CPU monitor capture writes GIF");
            Check(Decode(monitorResult.File!).Frames[0].PixelWidth == (int)Math.Round(display.Bounds.Width * .25), "Monitor scale matches physical dimensions");
            var area = new GifRecordingSession(new() { Folder = run, GifSeconds = 5, GifFps = 15, IncludeCursor = false }, CaptureSource.Area);
            var bounds = new Int32Rect(display.Bounds.X + 10, display.Bounds.Y + 10, 200, 100);
            var areaTask = area.RunAsync(0, false, bounds, default); var areaResult = await areaTask;
            Check(areaResult.File is not null && Decode(areaResult.File).Frames[0].PixelWidth == 200, "Area capture auto-stops at selected five-second limit");
            var duration = Decode(areaResult.File!).Frames.Sum(f => Convert.ToInt32(((BitmapMetadata)f.Metadata).GetQuery("/grctlext/Delay")));
            Check(duration is >= 480 and <= 505, "Real 15FPS animation preserves five-second timing");
            var indicator = new GifRecordingWindow(theme, localization.T, () => { }, () => { }); indicator.Show();
            Check(indicator.Failure is null, "Recording indicator is excluded from screen capture");
            indicator.Update(new(TimeSpan.FromSeconds(1), 200, 100, false, 0, false, 0)); await Task.Delay(100);
            // Protected windows cannot be captured directly; inspect UI render instead.
            var render = new RenderTargetBitmap((int)indicator.ActualWidth, (int)indicator.ActualHeight, 96, 96, PixelFormats.Pbgra32); render.Render(indicator);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render)); using (var file = File.Create(Path.Combine(run, "indicator.png"))) png.Save(file);
            indicator.Finish();
        }
        finally { theme.Close(); }
    }
}
