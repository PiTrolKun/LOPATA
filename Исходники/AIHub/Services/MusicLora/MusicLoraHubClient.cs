using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace AIHub.Services.MusicLora;

/// <summary>Public metadata and bounded byte ranges only. Never runs repository code or downloads full weights.</summary>
internal sealed class MusicLoraHubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _requests = new(8, 8);
    internal MusicLoraHubClient(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(35) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LOPATA-MusicLoRA/1.0");
    }
    internal static bool Repository(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,95}/[A-Za-z0-9][A-Za-z0-9_.-]{0,95}\z")
        && !value.Split('/').Any(p => p is "." or "..");
    internal static string? ExactRepository(string text)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) {
            if (uri.Scheme != "https" || uri.Host != "huggingface.co" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
                throw new InvalidDataException("Music.Lora.Search.SourceUnsupported");
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2) throw new InvalidDataException("Music.Lora.Search.SourceUnsupported");
            var repo = string.Join('/', parts.Take(2)); if (Repository(repo)) return repo;
            throw new InvalidDataException("Music.Lora.Search.SourceUnsupported");
        }
        return Repository(text) ? text : null;
    }
    internal static string Resolve(string repo, string revision, string file)
    {
        if (!Repository(repo) || !Regex.IsMatch(revision, @"\A[a-f0-9]{40,64}\z")
            || file.Length > 500 || file.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0))
            throw new InvalidDataException("Music.Lora.Invalid");
        return "https://huggingface.co/" + repo + "/resolve/" + revision + "/" + string.Join('/', file.Split('/').Select(Uri.EscapeDataString));
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) {
            response.Dispose(); throw new HttpRequestException("Music.Lora.Search.RateLimit", null, HttpStatusCode.TooManyRequests);
        }
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }
    private static async Task<byte[]> LimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Music.Lora.Search.ResponseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var memory = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) {
            if (memory.Length + count > limit) throw new InvalidDataException("Music.Lora.Search.ResponseTooLarge");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    internal async Task<JsonElement> JsonAsync(string route, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/models" + route);
        using var response = await SendAsync(request, token);
        using var json = JsonDocument.Parse(await LimitedAsync(response, 4000000, token)); return json.RootElement.Clone();
        } finally { _requests.Release(); }
    }
    internal async IAsyncEnumerable<JsonElement> PagesAsync(string route, [EnumeratorCancellation] CancellationToken token)
    {
        string? next = "https://huggingface.co/api/models" + route; var visited = new HashSet<string>();
        while (next is not null && visited.Add(next)) {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "huggingface.co"
                || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/api/models")
                throw new InvalidDataException("Music.Lora.Search.SourceUnsupported");
            JsonElement page;
            await _requests.WaitAsync(token);
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri); using var response = await SendAsync(request, token);
                using var json = JsonDocument.Parse(await LimitedAsync(response, 4000000, token)); page = json.RootElement.Clone();
                next = null;
                if (response.Headers.TryGetValues("Link", out var links)) {
                    var match = Regex.Match(string.Join(",", links), "<([^>]+)>;\\s*rel=\"?next\"?", RegexOptions.IgnoreCase);
                    if (match.Success) next = new Uri(uri, match.Groups[1].Value).AbsoluteUri;
                }
            } finally { _requests.Release(); }
            yield return page;
        }
    }
    internal async Task<byte[]> FileAsync(string url, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); using var response = await SendAsync(request, token);
        return await LimitedAsync(response, 130000, token);
        } finally { _requests.Release(); }
    }
    internal async Task<string> TextAsync(string repo, string revision, string path, CancellationToken token)
        => Encoding.UTF8.GetString(await FileAsync(Resolve(repo, revision, path), token));
    private async Task<byte[]> RangeAsync(string url, long first, long last, long total, CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Range = new RangeHeaderValue(first, last);
        request.Headers.AcceptEncoding.ParseAdd("identity"); using var response = await SendAsync(request, token);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != first || range.To != last || range.Length != total)
            throw new InvalidDataException("Music.Lora.Search.RangeUnsupported");
        var bytes = await LimitedAsync(response, checked((int)(last - first + 1)), token);
        if (bytes.LongLength != last - first + 1) throw new InvalidDataException("Music.Lora.Search.RangeUnsupported");
        return bytes;
        } finally { _requests.Release(); }
    }
    internal async Task<JsonElement> HeaderAsync(string url, long size, CancellationToken token)
    {
        var prefix = await RangeAsync(url, 0, 7, size, token); var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (length is < 2 or > 2000000 || length + 8 >= (ulong)size) throw new InvalidDataException("Music.Lora.Search.InvalidHeader");
        using var json = JsonDocument.Parse(await RangeAsync(url, 8, (long)length + 7, size, token));
        MusicLoraTensorRules.ValidateStorage(json.RootElement, size - (long)length - 8); return json.RootElement.Clone();
    }
    public void Dispose() { _http.Dispose(); _requests.Dispose(); }
}
