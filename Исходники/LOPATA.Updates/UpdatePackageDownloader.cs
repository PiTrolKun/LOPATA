using System.Net;
using System.Net.Http.Headers;

namespace Lopata.Updates;

public sealed record UpdateTransferProgress(string Package, long StoredBytes, long TotalBytes, string Stage);

public sealed class UpdatePackageDownloader(HttpClient http, string cacheDirectory, long segmentBytes = 8L * 1024 * 1024)
{
    public int MaximumParallelConnections { get; set; }

    public async Task<string> DownloadAsync(UpdateManifest manifest, UpdatePackage package,
        IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        using var connections = new UpdateDownloadConnections(UpdateDownloadSettings.Resolve(MaximumParallelConnections, package.Size));
        return await DownloadCoreAsync(manifest, package, connections, progress, token);
    }

    public async Task<IReadOnlyDictionary<string, string>> DownloadManyAsync(UpdateManifest manifest,
        IReadOnlyList<UpdatePackage> packages, IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        manifest.Validate();
        if (packages.Any(p => !manifest.Packages.Contains(p)) || packages.Select(p => p.Id).Distinct().Count() != packages.Count)
            throw new InvalidDataException("Unknown or duplicate update package.");
        if (packages.Count == 0) return new Dictionary<string, string>();
        Directory.CreateDirectory(cacheDirectory);
        // Reserve assembly space for concurrent transfers instead of racing per-file free-space checks.
        long required = 16L * 1024 * 1024;
        foreach (var package in packages)
        {
            var path = SafeUpdatePath.Resolve(cacheDirectory, package.Id);
            if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, package.Size, package.Sha256, token))
                required = checked(required + 2 * package.Size);
        }
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!).AvailableFreeSpace < required)
            throw new IOException("Insufficient disk space for parallel package downloads and assembly.");
        using var connections = new UpdateDownloadConnections(UpdateDownloadSettings.Resolve(MaximumParallelConnections, packages.Sum(p => p.Size)));
        var paths = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(packages,
            new ParallelOptions { MaxDegreeOfParallelism = connections.Count, CancellationToken = token },
            async (package, ct) => paths[package.Id] = await DownloadCoreAsync(manifest, package, connections, progress, ct));
        return paths;
    }

    private async Task<string> DownloadCoreAsync(UpdateManifest manifest, UpdatePackage package,
        UpdateDownloadConnections connections, IProgress<UpdateTransferProgress>? progress, CancellationToken token)
    {
        manifest.Validate();
        if (!manifest.Packages.Contains(package)) throw new InvalidDataException("Unknown update package.");
        var path = SafeUpdatePath.Resolve(cacheDirectory, package.Id);
        Directory.CreateDirectory(cacheDirectory);
        var partial = path + ".part";
        var lockPath = path + ".lock";
        SafeUpdatePath.RejectLinks(lockPath);
        await using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            if (await UpdatePlanner.MatchesAsync(path, package.Size, package.Sha256, token))
            {
                progress?.Report(new(package.Id, package.Size, package.Size, "verified"));
                return path;
            }
            File.Delete(path);
        }
        SafeUpdatePath.RejectLinks(partial);
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > package.Size) { File.Delete(partial); offset = 0; }
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cacheDirectory))!).AvailableFreeSpace
            < 2 * package.Size - offset + 16L * 1024 * 1024)
            throw new IOException("Insufficient disk space for package download.");
        var ranges = new UpdateRangeDownload(http, connections, Math.Max(1, segmentBytes));
        if (offset < package.Size && !await ranges.TryDownloadAsync(package, partial, progress, token))
        {
            using var connection = await connections.EnterAsync(token);
            using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
            request.Headers.UserAgent.ParseAdd("LOPATA-FileUpdater/1.0");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Any(e => e != "identity"))
                throw new InvalidDataException("Encoded package response cannot be resumed safely.");
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.Unit != "bytes" || range.From != offset || range.To != package.Size - 1 || range.Length != package.Size)
                    throw new InvalidDataException("Package response has an incorrect byte range.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
            else throw new InvalidDataException("Unexpected package download response.");
            if (response.Content.Headers.ContentLength is { } length && length != package.Size - offset)
                throw new InvalidDataException("Package response has an incorrect size.");
            await using (var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
                FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                if (output.Length != offset) throw new IOException("Partial package changed before resume.");
                output.Position = offset;
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (output.Position + read > package.Size) throw new InvalidDataException("Package response exceeds declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report(new(package.Id, output.Position, package.Size, "downloading"));
                }
                output.Flush(true);
                if (output.Length != package.Size) throw new IOException("Package download was interrupted.");
            }
        }
        progress?.Report(new(package.Id, package.Size, package.Size, "verifying"));
        if (!await UpdatePlanner.MatchesAsync(partial, package.Size, package.Sha256, token))
        {
            File.Delete(partial);
            ranges.ClearParts(partial, package.Size);
            throw new InvalidDataException("Package checksum mismatch. Retry the download.");
        }
        SafeUpdatePath.RejectLinks(path);
        File.Move(partial, path);
        ranges.ClearParts(partial, package.Size);
        return path;
    }
}
