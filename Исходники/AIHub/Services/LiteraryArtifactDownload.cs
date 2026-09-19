using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Pinned artifacts, resumable partial files and atomic promotion after verification.</summary>
public static class LiteraryArtifactDownload
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public static Task<bool> ValidAsync(string path, long size, string hash, string algorithm, CancellationToken ct, bool forceVerification = false) =>
        LiteraryFileVerification.Shared.ValidAsync(path, size, hash, algorithm, ct, forceVerification);

    public static async Task GetAsync(Uri uri, string path, long size, string hash, string algorithm,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (await ValidAsync(path, size, hash, algorithm, ct)) { progress?.Report(100); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        if (File.Exists(partial) && new FileInfo(partial).Length >= size)
        {
            if (await ValidAsync(partial, size, hash, algorithm, ct, true)) { File.Move(partial, path, true); return; }
            File.Delete(partial);
        }
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (disk.AvailableFreeSpace < size - offset + 64 * 1024 * 1024) throw new IOException("Insufficient disk space.");
        if (size >= 64L * 1024 * 1024)
        {
            var downloader = new SegmentedModelFileDownloader(Http)
            { MaximumParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads?.MaximumParallelConnections ?? 0 };
            await downloader.DownloadAsync(new ManagedModelArtifactCard { ModelArtifactId = "literary", DisplayName = Path.GetFileName(path) },
                new ManagedModelArtifactFile { RelativePath = Path.GetFileName(path), SourceUrl = uri.AbsoluteUri, SizeBytes = size, Sha256 = hash },
                path, 0, new InlineProgress<ManagedModelDownloadProgress>(p => progress?.Report(100d * p.DownloadedBytes / size)), ct);
            if (!await ValidAsync(partial, size, hash, algorithm, ct, true))
            { File.Delete(partial); throw new InvalidDataException("Downloaded file checksum mismatch."); }
            File.Move(partial, path, true); return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange is not { } range || range.From != offset || range.Length != size)
                throw new InvalidDataException("Unexpected download range.");
        }
        else offset = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[262144]; int count;
            while (true)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                count = await input.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                offset += count;
                if (offset > size) throw new InvalidDataException("Download exceeds expected size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                progress?.Report(100.0 * offset / size);
            }
        }
        if (!await ValidAsync(partial, size, hash, algorithm, ct, true))
        { File.Delete(partial); throw new InvalidDataException("Downloaded file checksum mismatch."); }
        File.Move(partial, path, true);
    }
}
