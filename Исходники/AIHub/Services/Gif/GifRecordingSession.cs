using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using AIHub.Models;

namespace AIHub.Services;

public sealed record GifRecordingProgress(TimeSpan Elapsed, int Width, int Height, bool Assembling, int Percent, bool ReducedDetail, int MissedFrames);
public sealed record GifRecordingResult(string? File, string CacheDirectory, int Frames, int MissedFrames, bool ReducedDetail, string Error);

/// <summary>One collector and one disk writer. At most one queued frame plus current work.
/// Stop completes the animation; cancel assembly retains the recoverable recording.</summary>
public sealed class GifRecordingSession
{
    private readonly CancellationTokenSource _stop = new(), _assembly = new();
    private readonly ScreenCaptureSettings _settings;
    private readonly string _stem;
    public string CacheDirectory { get; }
    public event Action<GifRecordingProgress>? Progress;
    public bool Assembling { get; private set; }
    public GifRecordingSession(ScreenCaptureSettings settings, CaptureSource source)
    {
        if (source == CaptureSource.Desktop) throw new ArgumentException("GIF does not capture all monitors.", nameof(source));
        _settings = JsonSerializer.Deserialize<ScreenCaptureSettings>(JsonSerializer.Serialize(settings))!; _settings.Normalize();
        var folder = ScreenshotFiles.Folder(_settings); Directory.CreateDirectory(folder);
        _stem = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_" + source.ToString().ToLowerInvariant() + "_" + Guid.NewGuid().ToString("N")[..8];
        CacheDirectory = Path.Combine(folder, ".lopata-gif-" + _stem);
    }
    public void Stop() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
    public void CancelAssembly() { Stop(); try { _assembly.Cancel(); } catch (ObjectDisposedException) { } }

    public async Task<GifRecordingResult> RunAsync(nint target, bool window, Int32Rect bounds, CancellationToken lifetime,
        Func<Int32Rect, int, bool, GifPixels>? cpuCapture = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, lifetime);
        var clock = Stopwatch.StartNew(); var store = new GifFrameStore(CacheDirectory, _settings.GifLoop);
        var channel = Channel.CreateBounded<(GifPixels Pixels, double At)>(new BoundedChannelOptions(1) { SingleWriter = true, SingleReader = true });
        var missed = 0; var reduced = false; var width = 0; var height = 0; var errorText = "";
        var writer = Task.Run(async () =>
        {
            try { await foreach (var frame in channel.Reader.ReadAllAsync()) store.Append(frame.Pixels, frame.At); }
            catch { limit.Cancel(); throw; }
        });
        void Receive(GifPixels frame)
        {
            if (width == 0) { width = frame.Width; height = frame.Height; }
            if ((long)width * height > 16_000_000) throw new InvalidOperationException("GIF exceeds the 16 megapixel recording limit. Choose a smaller output scale.");
            if (!channel.Writer.TryWrite((frame, clock.Elapsed.TotalMilliseconds))) missed++;
            Progress?.Invoke(new(clock.Elapsed, width, height, false, 0, reduced, missed));
        }
        try
        {
            limit.CancelAfter(TimeSpan.FromSeconds(_settings.GifSeconds));
            if (window || _settings.Processing == "gpu")
            {
                // WGC owns one persistent device/pool. Window collection also works when covered.
                var percent = _settings.GifScalePercent; var slow = 0; var prior = 0d;
                await new ScreenFrameCapture().RecordAsync(target, window, _settings.IncludeCursor, _settings.GifFps,
                    frame =>
                    {
                        var now = clock.Elapsed.TotalMilliseconds;
                        if (prior > 0 && now - prior > 1500d / _settings.GifFps)
                        { missed += Math.Max(1, (int)Math.Round((now - prior) * _settings.GifFps / 1000) - 1); slow++; }
                        else slow = 0;
                        Receive(GifPixels.From(frame, percent)); prior = now;
                        if (slow >= 3) { percent = Math.Max(1, _settings.GifScalePercent / 2); reduced = true; }
                    }, limit.Token, _settings.Processing == "cpu" || _settings.Processing == "auto" && ApplicationBackgroundOperations.Current?.IsRunning == true);
            }
            else
            {
                await Task.Run(() =>
                {
                    var percent = _settings.GifScalePercent; var slow = 0; var fast = 0; double due = 0;
                    while (!limit.IsCancellationRequested)
                    {
                        var remaining = due - clock.Elapsed.TotalMilliseconds;
                        if (remaining > 0) { if (limit.Token.WaitHandle.WaitOne((int)Math.Min(remaining + 1, 20))) break; continue; }
                        var start = clock.Elapsed.TotalMilliseconds;
                        Receive((cpuCapture ?? GifDesktopFrames.Grab)(bounds, percent, _settings.IncludeCursor));
                        var cost = clock.Elapsed.TotalMilliseconds - start; var interval = 1000d / _settings.GifFps;
                        slow = cost > interval * 0.8 ? slow + 1 : 0; fast = cost < interval * 0.35 ? fast + 1 : 0;
                        if (slow >= 3 && percent > Math.Max(1, _settings.GifScalePercent / 2))
                        { percent = Math.Max(1, _settings.GifScalePercent / 2); reduced = true; slow = fast = 0; }
                        else if (fast >= _settings.GifFps * 3 && percent < _settings.GifScalePercent)
                        { percent = _settings.GifScalePercent; slow = fast = 0; }
                        due += interval;
                        if (due < clock.Elapsed.TotalMilliseconds - interval)
                        { missed += Math.Max(1, (int)((clock.Elapsed.TotalMilliseconds - due) / interval)); due = clock.Elapsed.TotalMilliseconds; }
                    }
                });
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
        catch (Exception error) { errorText = error.Message; }
        finally { channel.Writer.TryComplete(); }
        try { await writer; } catch (Exception error) { errorText = error.Message; }
        store.Manifest.DurationMilliseconds = Math.Min(clock.Elapsed.TotalMilliseconds, _settings.GifSeconds * 1000d);
        store.Manifest.MissedFrames = missed; store.Manifest.ReducedDetail = reduced; store.Manifest.Error = errorText; store.SaveManifest();
        if (store.Manifest.Frames.Count == 0) { _stop.Dispose(); _assembly.Dispose(); return new(null, CacheDirectory, 0, missed, reduced, errorText); }
        Assembling = true;
        string? committedFile = null;
        try
        {
            using var assembleToken = CancellationTokenSource.CreateLinkedTokenSource(_assembly.Token, lifetime);
            var destination = Path.Combine(ScreenshotFiles.Folder(_settings), _stem + ".gif");
            await Task.Run(() => store.Assemble(destination, assembleToken.Token,
                percent => Progress?.Invoke(new(clock.Elapsed, width, height, true, percent, reduced, missed))));
            committedFile = destination;
            if (missed > 0 || reduced || errorText.Length > 0)
                File.WriteAllText(destination + ".json", JsonSerializer.Serialize(new
                { width, height, store.Manifest.DurationMilliseconds, missedFrames = missed, reducedDetail = reduced, error = errorText }));
            // Only files created by this recording are eligible for cleanup, after atomic output commit.
            Cleanup(store);
            return new(destination, CacheDirectory, store.Manifest.Frames.Count, missed, reduced, errorText);
        }
        catch (Exception error) { return new(committedFile, CacheDirectory, store.Manifest.Frames.Count, missed, reduced, error.Message); }
        finally { Assembling = false; _stop.Dispose(); _assembly.Dispose(); }
    }
    public static Task<string> RecoverAsync(string directory, CancellationToken token, Action<int>? progress = null) => Task.Run(() =>
    {
        var store = GifFrameStore.Open(directory); var folder = Directory.GetParent(store.DirectoryPath)?.FullName ?? throw new IOException("No recovery folder.");
        var path = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff") + "_recovered_" + Guid.NewGuid().ToString("N")[..8] + ".gif");
        store.Assemble(path, token, progress); return path;
    }, token);
    private static void Cleanup(GifFrameStore store)
    {
        try
        {
            foreach (var entry in store.Manifest.Frames) File.Delete(Path.Combine(store.DirectoryPath, entry.Name));
            File.Delete(Path.Combine(store.DirectoryPath, "recording.json")); Directory.Delete(store.DirectoryPath); // Nonrecursive; unknown files survive.
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
