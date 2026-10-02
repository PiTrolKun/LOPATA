using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media.Imaging;
using AIHub.Models;

namespace AIHub.Services;

public sealed record VideoRecordingProgress(TimeSpan Elapsed, int Width, int Height, bool Assembling, int Percent, bool ReducedDetail, int MissedFrames);
public sealed record VideoRecordingResult(string? File, string CacheDirectory, int Frames, int MissedFrames, bool ReducedDetail, string Error, string EncodingFallback = "");

public sealed class VideoRecordingSession
{
    private readonly ScreenCaptureSettings _settings;
    private readonly string _stem;
    private readonly CancellationTokenSource _stop = new(), _assembly = new();
    public string CacheDirectory { get; }
    public bool Assembling { get; private set; }
    public event Action<VideoRecordingProgress>? Progress;
    public VideoRecordingSession(ScreenCaptureSettings settings, CaptureSource source)
    {
        _settings = JsonSerializer.Deserialize<ScreenCaptureSettings>(JsonSerializer.Serialize(settings))!; _settings.Normalize();
        var folder = ScreenshotFiles.Folder(_settings); Directory.CreateDirectory(folder);
        _stem = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_" + source.ToString().ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
        CacheDirectory = Path.Combine(folder, ".lopata-video-" + _stem);
    }
    public void Stop() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    public void CancelAssembly() { Stop(); try { _assembly.Cancel(); } catch (ObjectDisposedException) { } }
    public async Task<VideoRecordingResult> RunAsync(nint target, bool window, Int32Rect bounds, CancellationToken lifetime,
        Func<Int32Rect, int, bool, GifPixels>? cpuCapture = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, lifetime);
        var epoch = Stopwatch.GetTimestamp(); var clock = Stopwatch.StartNew();
        var store = new VideoFrameStore(CacheDirectory, _settings);
        if (!window) (store.Manifest.Width, store.Manifest.Height) = VideoFrameStore.OutputSize(bounds.Width, bounds.Height, _settings.VideoQuality);
        var queue = Channel.CreateBounded<(GifPixels Pixels, double At)>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var frames = 0; var missed = 0; var reduced = false; var errorText = ""; double ended = 0; var overloaded = 0;
        var writer = Task.Run(async () =>
        {
            double diskCheck = 0;
            try
            {
                await foreach (var item in queue.Reader.ReadAllAsync())
                {
                    var checkpoint = item.At - diskCheck > 1000;
                    if (checkpoint)
                    {
                        var root = Path.GetPathRoot(store.DirectoryPath)!;
                        if (!root.StartsWith("\\\\", StringComparison.Ordinal))
                        { var drive = new DriveInfo(root); if (drive.IsReady && drive.AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("Video recording stopped: less than 512 MB of free disk space remains."); }
                        diskCheck = item.At;
                    }
                    store.Append(item.Pixels, item.At); Interlocked.Increment(ref frames);
                    // Crash recovery retains progress and quality warnings, not just frame paths.
                    if (checkpoint)
                    { store.Manifest.MissedFrames = Volatile.Read(ref missed); store.Manifest.ReducedDetail = reduced; store.Save(); }
                }
            }
            catch { stop.Cancel(); throw; }
        });
        using var audio = new VideoAudioCapture(store, epoch); audio.Failed += _ => stop.Cancel();
        void Receive(GifPixels frame)
        {
            if (store.Manifest.Width == 0)
            { (store.Manifest.Width, store.Manifest.Height) = VideoFrameStore.OutputSize(frame.Width, frame.Height, _settings.VideoQuality); store.Save(); }
            if (!queue.Writer.TryWrite((frame, clock.Elapsed.TotalMilliseconds))) { Interlocked.Increment(ref missed); overloaded++; }
            else overloaded = Math.Max(0, overloaded - 1);
            Progress?.Invoke(new(clock.Elapsed, store.Manifest.Width, store.Manifest.Height, false, 0, reduced, missed));
        }
        async Task CpuLoop()
        {
            await Task.Run(() =>
            {
                var requested = Math.Clamp((int)Math.Ceiling(Math.Min(store.Manifest.Width / (double)bounds.Width, store.Manifest.Height / (double)bounds.Height) * 100), 1, 100);
                var scale = new VideoCaptureScale(requested, _settings.VideoCompression == 100);
                var interval = 1000d / _settings.VideoFps; var due = clock.Elapsed.TotalMilliseconds;
                while (!stop.IsCancellationRequested)
                {
                    var wait = due - clock.Elapsed.TotalMilliseconds;
                    if (wait > 0) { stop.Token.WaitHandle.WaitOne((int)Math.Min(wait + 1, 20)); continue; }
                    var started = clock.Elapsed.TotalMilliseconds;
                    Receive((cpuCapture ?? GifDesktopFrames.Grab)(bounds, scale.Percent, _settings.IncludeCursor));
                    var cost = clock.Elapsed.TotalMilliseconds - started;
                    scale.Observe(clock.Elapsed.TotalMilliseconds, cost, interval, overloaded > 0);
                    reduced |= scale.Percent < requested;
                    due += interval;
                    if (due < clock.Elapsed.TotalMilliseconds - interval)
                    { Interlocked.Add(ref missed, Math.Max(1, (int)((clock.Elapsed.TotalMilliseconds - due) / interval))); due = clock.Elapsed.TotalMilliseconds; }
                }
            });
        }
        try
        {
            store.Save(); await audio.StartAsync(_settings);
            var busy = ApplicationBackgroundOperations.Current?.IsRunning == true;
            var gpu = _settings.Processing == "gpu" || _settings.Processing == "auto" && !busy;
            if (window || gpu && target != 0)
            {
                VideoCaptureScale? scale = null; BitmapSource? cachedSource = null; GifPixels? cachedPixels = null; var cachedPercent = 0;
                var previous = clock.Elapsed.TotalMilliseconds;
                try
                {
                    await new ScreenFrameCapture().RecordAsync(target, window, _settings.IncludeCursor, _settings.VideoFps, frame =>
                    {
                        var now = clock.Elapsed.TotalMilliseconds; var gap = now - previous;
                        if (cachedSource is not null && gap > 1500d / _settings.VideoFps) Interlocked.Add(ref missed, Math.Max(1, (int)Math.Round(gap * _settings.VideoFps / 1000) - 1));
                        previous = now;
                        if (store.Manifest.Width == 0) (store.Manifest.Width, store.Manifest.Height) = VideoFrameStore.OutputSize(frame.PixelWidth, frame.PixelHeight, _settings.VideoQuality);
                        var requested = Math.Clamp((int)Math.Ceiling(Math.Min(store.Manifest.Width / (double)frame.PixelWidth, store.Manifest.Height / (double)frame.PixelHeight) * 100), 1, 100);
                        scale ??= new VideoCaptureScale(requested, _settings.VideoCompression == 100);
                        // WGC repeats the same frozen bitmap on a static desktop. Reuse its CPU pixels.
                        if (!ReferenceEquals(frame, cachedSource) || cachedPercent != scale.Percent)
                        { cachedPixels = GifPixels.From(frame, scale.Percent); cachedSource = frame; cachedPercent = scale.Percent; }
                        Receive(cachedPixels!);
                        scale.Observe(clock.Elapsed.TotalMilliseconds, clock.Elapsed.TotalMilliseconds - now, 1000d / _settings.VideoFps, overloaded > 0);
                        reduced |= scale.Percent < requested;
                    }, stop.Token, !gpu);
                }
                catch (Exception error) when (!window && !stop.IsCancellationRequested && error is not OperationCanceledException)
                { errorText = "GPU capture switched to CPU: " + error.Message; reduced = true; await CpuLoop(); }
            }
            else await CpuLoop();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { errorText += "\n" + error.Message; }
        finally { ended = clock.Elapsed.TotalMilliseconds; queue.Writer.TryComplete(); await audio.StopAsync(); }
        try { await writer; } catch (Exception error) { errorText += "\n" + error.Message; }
        errorText += audio.Error;
        store.Manifest.DurationMilliseconds = ended; store.Manifest.MissedFrames = missed; store.Manifest.ReducedDetail = reduced; store.Manifest.Error = errorText;
        // Settings may change while recording. This session and recovery use their original snapshot.
        if (_settings.Processing == "auto") _settings.Processing = ApplicationBackgroundOperations.Current?.IsRunning == true ? "cpu" : "gpu";
        store.Save();
        if (frames == 0) { _stop.Dispose(); _assembly.Dispose(); return new(null, CacheDirectory, 0, missed, reduced, errorText); }
        Assembling = true; string? committed = null;
        try
        {
            using var assemble = CancellationTokenSource.CreateLinkedTokenSource(_assembly.Token, lifetime);
            var destination = Path.Combine(ScreenshotFiles.Folder(_settings), _stem + "." + _settings.VideoFormat);
            var result = await VideoEncoder.AssembleAsync(store, destination, assemble.Token, percent => Progress?.Invoke(new(TimeSpan.FromMilliseconds(ended), store.Manifest.Width, store.Manifest.Height, true, percent, reduced, missed)));
            committed = result.Path;
            File.WriteAllText(destination + ".json", JsonSerializer.Serialize(new { store.Manifest.Width, store.Manifest.Height, store.Manifest.MinFrameWidth, store.Manifest.MinFrameHeight, ended, missedFrames = missed, reducedDetail = reduced, error = errorText, result.Encoder, result.Fallback }));
            store.Cleanup(); return new(destination, CacheDirectory, frames, missed, reduced, errorText, result.Fallback);
        }
        catch (Exception error) { return new(committed, CacheDirectory, frames, missed, reduced, errorText + "\n" + error.Message); }
        finally { Assembling = false; _stop.Dispose(); _assembly.Dispose(); }
    }
    public static async Task<string> RecoverAsync(string directory, CancellationToken token, Action<int>? progress = null)
    {
        var store = VideoFrameStore.Open(directory); var last = store.Frames().LastOrDefault() ?? throw new IOException("No recoverable video frames.");
        store.Manifest.DurationMilliseconds = Math.Max(store.Manifest.DurationMilliseconds, last.At + 1000d / store.Manifest.Settings.VideoFps);
        foreach (var audio in store.Manifest.Audio)
        { var file = new FileInfo(Path.Combine(store.DirectoryPath, audio.Name)); if (file.Exists) store.Manifest.DurationMilliseconds = Math.Max(store.Manifest.DurationMilliseconds, file.Length * 1000d / audio.Rate / audio.BlockAlign); }
        var folder = Directory.GetParent(store.DirectoryPath)!.FullName;
        var destination = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_recovered_" + Guid.NewGuid().ToString("N")[..8] + "." + store.Manifest.Settings.VideoFormat);
        var result = await VideoEncoder.AssembleAsync(store, destination, token, progress);
        File.WriteAllText(destination + ".json", JsonSerializer.Serialize(new { recovered = true, store.Manifest, result.Encoder, result.Fallback })); return destination;
    }
}
