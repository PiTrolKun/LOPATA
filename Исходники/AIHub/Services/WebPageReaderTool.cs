using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class WebPageReaderTool
{
    private const int MaxReturnedCharacters = 6000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private static readonly HttpClient PreviewHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public async Task<WebPageReadResponse> ReadAsync(
        string url,
        StorageSettings storageSettings,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeHttpUrl(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl);
        request.Headers.UserAgent.ParseAdd("LOPATA/0.1 (+https://github.com/PiTrolKun/LOPATA)");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var title = ExtractTitle(html);
        var text = ExtractText(html);
        var candidateFileUrls = ExtractCandidateFileUrls(html, normalizedUrl);
        if (text.Length > MaxReturnedCharacters)
        {
            text = text[..MaxReturnedCharacters] + Environment.NewLine + "...";
        }

        var result = new WebPageReadResponse
        {
            Url = normalizedUrl.ToString(),
            Title = title,
            Text = text,
            CandidateFileUrls = candidateFileUrls
        };

        var path = WebToolPathService.CreateStampedPath(
            WebToolPathService.GetPagesDirectory(storageSettings),
            CreateShortHash(normalizedUrl.ToString()),
            ".json");
        result.SavedPath = path;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8, cancellationToken);
        return result;
    }

    internal async Task<WebPageReadResponse> ReadPreviewAsync(string url, CancellationToken token)
    {
        var address = NormalizeHttpUrl(url);
        if (address.UserInfo.Length > 0) throw new InvalidOperationException("VisualConstructor.ResearchUrlInvalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("LOPATA-TechnicalResearch/1.0");
        request.Headers.Accept.ParseAdd("text/html,text/plain,application/xhtml+xml");
        using var response = await PreviewHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && type != "application/xhtml+xml")
            throw new InvalidOperationException("VisualConstructor.ResearchPageType");
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, deadline.Token);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        var isHtml = type is "text/html" or "application/xhtml+xml";
        var text = isHtml ? ExtractText(body) : body;
        return new WebPageReadResponse
        {
            Url = address.AbsoluteUri, Title = isHtml ? ExtractTitle(body) : "",
            Text = text[..Math.Min(text.Length, MaxReturnedCharacters)]
        };
    }

    private static Uri NormalizeHttpUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Only http/https URLs are supported.");
        }

        return uri;
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success
            ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim()
            : string.Empty;
    }

    private static string ExtractText(string html)
    {
        var text = ScriptRegex().Replace(html, " ");
        text = StyleRegex().Replace(text, " ");
        text = TagRegex().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }

    private static List<string> ExtractCandidateFileUrls(string html, Uri baseUri)
    {
        return AttributeUrlRegex()
            .Matches(html)
            .SelectMany(match => SplitSrcSet(match.Groups["url"].Value))
            .Select(value => NormalizePageUrl(value, baseUri))
            .Where(url => !string.IsNullOrWhiteSpace(url) && IsDirectFileUrl(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    private static IEnumerable<string> SplitSrcSet(string value)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var url = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return System.Net.WebUtility.HtmlDecode(url);
            }
        }
    }

    private static string NormalizePageUrl(string value, Uri baseUri)
    {
        var trimmed = System.Net.WebUtility.HtmlDecode(value).Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = baseUri.Scheme + ":" + trimmed;
        }

        return Uri.TryCreate(baseUri, trimmed, out var uri) ? uri.ToString() : string.Empty;
    }

    private static bool IsDirectFileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".svg"
            or ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a"
            or ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv"
            or ".pdf" or ".txt" or ".json" or ".csv" or ".zip" or ".7z" or ".rar" or ".gz"
            or ".gguf" or ".bin";
    }

    private static string CreateShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<script[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<style[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("(?:href|src|content|srcset)=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AttributeUrlRegex();
}
