using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public interface IPublisherGitHubClient
{
    Task<List<PublisherRelease>> ReadAsync(string repository, CancellationToken token);
}

public sealed class PublisherGitHubClient(HttpClient http) : IPublisherGitHubClient
{
    public static string ParseRepository(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new PublisherException("Publisher.InvalidRepository");
        var pieces = uri.AbsolutePath.Trim('/').Split('/');
        if (pieces.Length != 2 && !(pieces.Length == 3 && pieces[2].Equals("releases", StringComparison.OrdinalIgnoreCase)))
            throw new PublisherException("Publisher.InvalidRepository");
        var repo = pieces[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? pieces[1][..^4] : pieces[1];
        if (!Regex.IsMatch(pieces[0], @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z")
            || !Regex.IsMatch(repo, @"\A[A-Za-z0-9_.-]{1,100}\z") || repo is "." or "..")
            throw new PublisherException("Publisher.InvalidRepository");
        return pieces[0] + "/" + repo;
    }

    public async Task<List<PublisherRelease>> ReadAsync(string repository, CancellationToken token)
    {
        if (ParseRepository("https://github.com/" + repository) != repository)
            throw new PublisherException("Publisher.InvalidRepository");
        var releases = new Dictionary<long, PublisherRelease>();
        for (var page = 1; ; page++)
        {
            // ResponseHeadersRead stops HttpClient's timeout before the body arrives.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(35) : http.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("LOPATA-Publisher/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new PublisherException("Publisher.RateLimit");
            if (response.StatusCode == HttpStatusCode.NotFound) throw new PublisherException("Publisher.RepositoryNotFound");
            if (!response.IsSuccessStatusCode) throw new PublisherException("Publisher.NetworkError");
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, deadline.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean() || item.GetProperty("published_at").ValueKind == JsonValueKind.Null) continue;
                var id = item.GetProperty("id").GetInt64();
                var url = item.GetProperty("html_url").GetString()!;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var releaseUri) || releaseUri.Scheme != "https"
                    || releaseUri.Host != "github.com" || !releaseUri.IsDefaultPort || releaseUri.UserInfo.Length != 0
                    || !releaseUri.AbsolutePath.StartsWith("/" + repository + "/releases/tag/", StringComparison.OrdinalIgnoreCase))
                    throw new PublisherException("Publisher.InvalidResponse");
                var tag = item.GetProperty("tag_name").GetString() ?? "";
                releases[id] = new(id, tag, item.GetProperty("name").GetString() is { Length: > 0 } name ? name : tag,
                    item.GetProperty("body").GetString() ?? "", item.GetProperty("published_at").GetDateTimeOffset(), url);
            }
            if (json.RootElement.GetArrayLength() < 100) break;
            token.ThrowIfCancellationRequested();
        }
        return releases.Values.OrderBy(r => r.PublishedAt).ThenBy(r => r.Id).ToList();
    }
}
