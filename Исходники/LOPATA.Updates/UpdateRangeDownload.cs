using System.Net;
using System.Net.Http.Headers;

namespace Lopata.Updates;

/// <summary>Fixed ranges survive cancellation and changes to the connection preference.</summary>
internal sealed class UpdateRangeDownload(UpdateHttpTransfer network, UpdateDownloadConnections connections, long segmentBytes)
{
    private const int BufferSize = 1024 * 1024;

    public async Task<bool> TryDownloadAsync(UpdatePackage package, string partial,
        IProgress<UpdateTransferProgress>? progress, CancellationToken token)
    {
        var folder = partial + ".ranges";
        SafeUpdatePath.RejectLinks(folder);
        if (package.Size <= segmentBytes || (connections.Count == 1 && !Directory.Exists(folder))) return false;
        using (await connections.EnterAsync(token))
        using (var request = Request(package.Url, 0, 0))
        using (var response = await network.SendAsync(request, token))
        {
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.OK) return false;
            Validate(response, 0, 0, package.Size);
        }
        Directory.CreateDirectory(folder);
        var count = checked((int)((package.Size + segmentBytes - 1) / segmentBytes));
        var stored = new long[count];
        var prefix = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        for (var i = 0; i < count; i++)
        {
            var start = i * segmentBytes;
            var length = Math.Min(segmentBytes, package.Size - start);
            var path = PartPath(folder, start);
            SafeUpdatePath.RejectLinks(path);
            var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (bytes > length) { File.Delete(path); bytes = 0; }
            var available = Math.Clamp(prefix - start, 0, length);
            if (bytes < available)
            {
                await using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                input.Position = start + bytes; output.Position = bytes;
                await CopyExactlyAsync(input, output, available - bytes, token);
                output.Flush(true);
                bytes = available;
            }
            stored[i] = bytes;
        }
        var sync = new object();
        void Report(int index, long bytes)
        {
            lock (sync)
            {
                stored[index] = bytes;
                progress?.Report(new(package.Id, stored.Sum(), package.Size, "downloading"));
            }
        }
        Report(0, stored[0]);
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = connections.Count, CancellationToken = token },
            async (index, ct) =>
            {
                var start = index * segmentBytes;
                var end = Math.Min(start + segmentBytes, package.Size) - 1;
                var length = end - start + 1;
                var existing = stored[index];
                if (existing == length) return;
                using var lease = await connections.EnterAsync(ct);
                using var request = Request(package.Url, start + existing, end);
                using var response = await network.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                Validate(response, start + existing, end, package.Size);
                var path = PartPath(folder, start);
                SafeUpdatePath.RejectLinks(path);
                await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous);
                if (output.Length != existing) throw new IOException("Partial range changed before resume.");
                output.Position = existing;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await network.ReadAsync(input, buffer, ct)) != 0)
                {
                    if (output.Position + read > length) throw new InvalidDataException("Range exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    Report(index, output.Position);
                }
                output.Flush(true);
                if (output.Length != length) throw new IOException("Package range download was interrupted.");
            });
        progress?.Report(new(package.Id, package.Size, package.Size, "assembling"));
        var assembled = partial + ".assembling";
        SafeUpdatePath.RejectLinks(assembled);
        await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous))
        {
            for (var i = 0; i < count; i++)
            {
                var start = i * segmentBytes;
                var path = PartPath(folder, start);
                SafeUpdatePath.RejectLinks(path);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = Math.Min(segmentBytes, package.Size - start);
                if (input.Length != length) throw new IOException("Partial range changed before assembly.");
                await CopyExactlyAsync(input, output, length, token);
            }
            output.Flush(true);
        }
        SafeUpdatePath.RejectLinks(partial);
        File.Move(assembled, partial, overwrite: true);
        return true;
    }

    public void ClearParts(string partial, long size)
    {
        var folder = partial + ".ranges";
        SafeUpdatePath.RejectLinks(folder);
        for (long start = 0; Directory.Exists(folder) && start < size; start += segmentBytes)
        {
            var path = PartPath(folder, start);
            SafeUpdatePath.RejectLinks(path);
            File.Delete(path);
        }
        var assembled = partial + ".assembling";
        SafeUpdatePath.RejectLinks(assembled);
        File.Delete(assembled);
    }

    private static string PartPath(string folder, long start) => Path.Combine(folder, start + ".part");
    private static HttpRequestMessage Request(string url, long start, long end)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LOPATA-FileUpdater/1.0");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.Range = new RangeHeaderValue(start, end);
        return request;
    }

    private static void Validate(HttpResponseMessage response, long start, long end, long total)
    {
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
            || range.From != start || range.To != end || range.Length != total
            || response.Content.Headers.ContentEncoding.Any(e => e != "identity")
            || (response.Content.Headers.ContentLength is { } length && length != end - start + 1))
            throw new InvalidDataException("Package response has an incorrect byte range or encoding.");
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), token);
            if (read == 0) throw new EndOfStreamException("Incomplete package range.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            length -= read;
        }
    }
}
