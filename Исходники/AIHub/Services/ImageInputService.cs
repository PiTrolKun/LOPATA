using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using AIHub.Models;

namespace AIHub.Services;

public sealed record ImageInput(string? FilePath = null, byte[]? Png = null, Uri? Url = null);
public sealed class ImageInputException(string key) : Exception(key) { public string Key { get; } = key; }

public sealed class ImageInputService(ImageAssetStore assets, HttpClient http)
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;

    public async Task<ImageAnalysisFilePassport> ImportAsync(ImageInput input, CancellationToken token)
    {
        if (input.FilePath is not null)
            return await new ImageAnalysisFileValidationService().ValidateAsync(input.FilePath, token);
        var path = assets.Allocate(".part");
        try
        {
            if (input.Png is not null)
            {
                if (input.Png.Length > MaxBytes) throw new ImageInputException("ImageInput.TooLarge");
                await File.WriteAllBytesAsync(path, input.Png, token);
            }
            else if (input.Url is not null) await DownloadAsync(input.Url, path, token);
            else throw new ImageInputException("ImageInput.Unsupported");
            token.ThrowIfCancellationRequested();
            var extension = await Task.Run(() => Identify(path), token);
            var finalPath = assets.Allocate(extension);
            File.Move(path, finalPath);
            path = finalPath;
            var passport = await new ImageAnalysisFileValidationService().ValidateAsync(path, token);
            passport.StorageKind = ImageAssetKinds.Temporary;
            return passport;
        }
        catch { assets.DeleteTemporary(path); throw; }
    }

    private async Task DownloadAsync(Uri uri, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                if (!IsHttp(uri)) throw new ImageInputException("ImageInput.Unsupported");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null || redirects == 5) throw new ImageInputException("ImageInput.DownloadFailed");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new ImageInputException("ImageInput.DownloadFailed");
                if (response.Content.Headers.ContentLength > MaxBytes) throw new ImageInputException("ImageInput.TooLarge");
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is "text/html" or "application/xhtml+xml") throw new ImageInputException("ImageInput.NotImage");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920]; var total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > MaxBytes) throw new ImageInputException("ImageInput.TooLarge");
                    await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new ImageInputException("ImageInput.Timeout"); }
        catch (HttpRequestException) { throw new ImageInputException("ImageInput.DownloadFailed"); }
    }

    public static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme is "http" or "https") && string.IsNullOrEmpty(uri.UserInfo);

    private static string Identify(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames.First();
            if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new ImageInputException("ImageInput.TooLarge");
            return decoder.CodecInfo?.FileExtensions.Split(',').Select(e => e.Trim().ToLowerInvariant())
                .FirstOrDefault(e => e is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp")
                ?? throw new ImageInputException("ImageInput.NotImage");
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or InvalidOperationException or ArgumentException)
        { throw new ImageInputException("ImageInput.NotImage"); }
    }
}
