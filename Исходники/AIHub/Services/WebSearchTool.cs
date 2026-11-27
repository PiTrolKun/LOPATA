using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class WebSearchTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly WebSearchRerankerService _rerankerService = new();

    public async Task<WebSearchResponse> SearchAsync(
        string query,
        StorageSettings storageSettings,
        CancellationToken cancellationToken,
        bool archiveAndRerank = true)
    {
        var searchAttempt = await TrySearchAsync(
            () => SearchDuckDuckGoAsync(query, "https://lite.duckduckgo.com/lite/?q=", cancellationToken),
            "DuckDuckGo Lite",
            cancellationToken);
        if (searchAttempt.Items.Count == 0)
        {
            await Task.Delay(350, cancellationToken);
            var retryAttempt = await TrySearchAsync(
                () => SearchDuckDuckGoAsync(query, "https://html.duckduckgo.com/html/?q=", cancellationToken),
                "DuckDuckGo HTML",
                cancellationToken);
            if (retryAttempt.Items.Count > searchAttempt.Items.Count)
            {
                searchAttempt = retryAttempt;
            }
        }

        if (searchAttempt.Items.Count == 0)
        {
            await Task.Delay(500, cancellationToken);
            var bingAttempt = await TrySearchAsync(
                () => SearchBingAsync(query, cancellationToken),
                "Bing HTML",
                cancellationToken);
            if (bingAttempt.Items.Count > searchAttempt.Items.Count)
            {
                searchAttempt = bingAttempt;
            }
        }

        var result = new WebSearchResponse
        {
            Query = query,
            Provider = searchAttempt.Provider,
            HttpStatusCode = searchAttempt.HttpStatusCode,
            Results = searchAttempt.Items
        };
        result.ResultCount = result.Results.Count;
        ApplySearchDiagnostics(result, searchAttempt.Html);
        if (!archiveAndRerank) return result;
        result.Rerank = await _rerankerService.RerankAsync(query, result.Results, storageSettings, cancellationToken);

        var path = WebToolPathService.CreateStampedPath(
            WebToolPathService.GetSearchDirectory(storageSettings),
            "search",
            ".json");
        result.SavedPath = path;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8, cancellationToken);
        return result;
    }

    private static async Task<SearchAttempt> TrySearchAsync(
        Func<Task<SearchAttempt>> search,
        string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await search();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new SearchAttempt(provider, 0, "provider_error: " + ex.Message, []);
        }
    }

    private async Task<SearchAttempt> SearchDuckDuckGoAsync(
        string query,
        string endpointPrefix,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(endpointPrefix + Uri.EscapeDataString(query));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AI_HUB/0.1");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var items = ResultRegex()
            .Matches(html)
            .Select((match, index) => new WebSearchResult
            {
                OriginalRank = index + 1,
                RerankedRank = index + 1,
                Title = CleanHtml(match.Groups["title"].Value),
                Url = DecodeDuckDuckGoUrl(match.Groups["href"].Value),
                Snippet = CleanHtml(match.Groups["snippet"].Value)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .Take(10)
            .ToList();

        return new SearchAttempt(
            endpointPrefix.Contains("html.duckduckgo.com", StringComparison.OrdinalIgnoreCase) ? "DuckDuckGo HTML" : "DuckDuckGo Lite",
            (int)response.StatusCode,
            html,
            items);
    }

    private async Task<SearchAttempt> SearchBingAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&setlang=en-US&cc=US");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AI_HUB/0.1");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8,ru;q=0.6");
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SearchAttempt("Bing HTML", (int)response.StatusCode, html, []);
        }

        var items = BingResultRegex()
            .Matches(html)
            .Select((match, index) => new WebSearchResult
            {
                OriginalRank = index + 1,
                RerankedRank = index + 1,
                Title = CleanHtml(match.Groups["title"].Value),
                Url = DecodeBingUrl(match.Groups["href"].Value),
                Snippet = CleanHtml(match.Groups["snippet"].Value)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Url)
                && item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !item.Url.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return new SearchAttempt("Bing HTML", (int)response.StatusCode, html, items);
    }

    private static void ApplySearchDiagnostics(WebSearchResponse result, string html)
    {
        if (result.Results.Count > 0)
        {
            result.Status = "ok";
            result.PossibleReason = "results_found";
            result.RecommendedNextSteps =
            [
                "Read the most relevant pages with web_read before summarizing.",
                "If results are broad, refine the query or use official sources."
            ];
            return;
        }

        result.Status = "empty";
        result.PossibleReason = DetectEmptyReason(html);
        result.RecommendedNextSteps =
        [
            "Do not claim that anything was found.",
            "Try a simpler query in the user's language.",
            "Try the same meaning in English.",
            "Remove or relax strict date filters.",
            "Try official-site queries such as site:nasa.gov or site:roscosmos.ru.",
            "If several different attempts are empty, explain that no confirmed result was found through current tools."
        ];
    }

    private static string DetectEmptyReason(string html)
    {
        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || html.Contains("anomaly", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Please prove", StringComparison.OrdinalIgnoreCase))
        {
            return "provider_blocked_or_challenged";
        }

        if (html.Contains("provider_error:", StringComparison.OrdinalIgnoreCase))
        {
            return "provider_error_or_timeout";
        }

        if (!html.Contains("result-link", StringComparison.OrdinalIgnoreCase)
            && html.Contains("DuckDuckGo", StringComparison.OrdinalIgnoreCase))
        {
            return "provider_empty_or_parser_mismatch";
        }

        return "no_results_or_query_too_narrow";
    }

    private static string CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = TagRegex().Replace(value, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private static string DecodeDuckDuckGoUrl(string href)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(href).Trim();
        if (decoded.StartsWith("//", StringComparison.Ordinal))
        {
            decoded = "https:" + decoded;
        }

        var marker = "uddg=";
        var markerIndex = decoded.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return decoded;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = decoded.IndexOf('&', valueStart);
        var encodedTarget = valueEnd < 0 ? decoded[valueStart..] : decoded[valueStart..valueEnd];
        return Uri.UnescapeDataString(encodedTarget);
    }

    private static string DecodeBingUrl(string href)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(href).Trim();
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            || !uri.Host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
        {
            return decoded;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var encodedTarget = query
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Equals("u", StringComparison.OrdinalIgnoreCase))
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(encodedTarget))
        {
            return decoded;
        }

        if (encodedTarget.StartsWith("a1", StringComparison.OrdinalIgnoreCase))
        {
            encodedTarget = encodedTarget[2..];
        }

        try
        {
            var padded = encodedTarget.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return decoded;
        }
    }

    [GeneratedRegex("<a[^>]+href=\"(?<href>[^\"]+)\"[^>]+class=['\"]result-link['\"][^>]*>(?<title>.*?)</a>.*?(?:<td class=['\"]result-snippet['\"]>(?<snippet>.*?)</td>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ResultRegex();

    [GeneratedRegex("<li class=\"b_algo\".*?<h2[^>]*>\\s*<a[^>]+href=\"(?<href>[^\"]+)\"[^>]*>(?<title>.*?)</a>.*?(?:<p[^>]*>(?<snippet>.*?)</p>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BingResultRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record SearchAttempt(string Provider, int HttpStatusCode, string Html, List<WebSearchResult> Items);
}
