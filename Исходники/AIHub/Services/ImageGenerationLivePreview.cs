using System.IO;
using System.Windows.Media.Imaging;

namespace AIHub.Services;

internal sealed class ImageGenerationLivePreview : IDisposable
{
    private readonly string _directory;
    private string? _lastFrame;

    private ImageGenerationLivePreview(string directory) => _directory = directory;

    // The native CLI interprets percent signs in the entire filename as part of its frame pattern.
    internal string OutputPattern => Path.Combine(_directory.Replace("%", "%%"), "frame_%03d.png");

    internal static ImageGenerationLivePreview? TryCreate()
    {
        try { return new(Directory.CreateTempSubdirectory("lopata-image-preview-").FullName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal async Task WatchAsync(IProgress<BitmapSource> progress, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                if (ReadLatest() is { } frame) progress.Report(frame);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal BitmapSource? ReadLatest()
    {
        try
        {
            var path = Directory.EnumerateFiles(_directory, "frame_*.png").Max(StringComparer.Ordinal);
            if (path is null || path == _lastFrame) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            _lastFrame = path;
            return frame;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            NotSupportedException or FormatException or ArgumentException)
        {
            // A frame may still be written. Leave the previous one visible and retry on the next tick.
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var frame in Directory.EnumerateFiles(_directory, "frame_*.png"))
                File.Delete(frame);
            Directory.Delete(_directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
