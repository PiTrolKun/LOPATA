using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AIHub.Services;

/// <summary>Acquire a pinned multipart profile; retain interrupted parts but never publish unchecked bytes.</summary>
internal static class PinnedPythonDownloader
{
    internal static async Task<IReadOnlyDictionary<string, string>> AcquireAsync(
        IReadOnlyList<PinnedPythonArtifact> artifacts, string cacheDirectory, HttpClient client,
        Action<long, long>? progress, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(cacheDirectory)) throw new InvalidDataException("An absolute Python cache directory is required.");
        if (artifacts.Any(artifact => !OfficialUri(artifact.Source)))
            throw new InvalidDataException("Python artifact source is outside official origins.");
        Directory.CreateDirectory(cacheDirectory);
        Lopata.Updates.SafeUpdatePath.RejectLinks(cacheDirectory);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = checked(artifacts.Sum(artifact => artifact.Bytes)); long completed = 0;
        foreach (var artifact in artifacts)
        {
            token.ThrowIfCancellationRequested();
            PythonWheelExtractor.RelativeDestination(artifact.FileName, wheelLayout: false);
            if (artifact.FileName.Contains('/')) throw new InvalidDataException("Python cache artifact must be a basename.");
            var path = Path.Combine(cacheDirectory, artifact.FileName); var part = path + ".part";
            Lopata.Updates.SafeUpdatePath.RejectLinks(path);
            Lopata.Updates.SafeUpdatePath.RejectLinks(part);
            if (!await MatchesAsync(path, artifact, token))
            {
                if (!await MatchesAsync(part, artifact, token))
                    await DownloadAsync(artifact, part, client, value => progress?.Invoke(completed + value, total), token);
                if (!await MatchesAsync(part, artifact, token))
                {
                    File.Delete(part);
                    throw new InvalidDataException("Python download differs from its pinned digest: " + artifact.FileName);
                }
                File.Move(part, path, overwrite: true);
            }
            result.Add(artifact.FileName, path);
            completed += artifact.Bytes; progress?.Invoke(completed, total);
        }
        return result;
    }

    private static async Task<bool> MatchesAsync(string path, PinnedPythonArtifact artifact, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        await using var file = File.OpenRead(path);
        return file.Length == artifact.Bytes && Convert.ToHexString(await SHA256.HashDataAsync(file, token))
            .Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAsync(PinnedPythonArtifact artifact, string path, HttpClient client,
        Action<long> progress, CancellationToken token)
    {
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset >= artifact.Bytes) { File.Delete(path); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Source);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && !OfficialUri(final))
            throw new InvalidDataException("Python artifact redirected outside official origins.");
        if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != offset || range.Length != artifact.Bytes
                || range.To is null || range.To >= artifact.Bytes)
                throw new InvalidDataException("Python resume range differs from the pinned artifact.");
        }
        else throw new InvalidDataException("Unexpected Python artifact HTTP status.");
        if (response.Content.Headers.ContentLength is { } length && length > artifact.Bytes - offset)
            throw new InvalidDataException("Python download exceeds its pinned size.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[128 * 1024]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (read > artifact.Bytes - offset) throw new InvalidDataException("Python download exceeds its pinned size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            offset += read; progress(offset);
        }
    }

    private static bool OfficialUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https"
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host is "www.python.org" or "files.pythonhosted.org" or "download.pytorch.org" or "download-r2.pytorch.org"
            || uri.Host == "repo.radeon.com" && uri.AbsolutePath.StartsWith("/rocm/windows/rocm-rel-7.2.1/", StringComparison.Ordinal)
                && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
}
