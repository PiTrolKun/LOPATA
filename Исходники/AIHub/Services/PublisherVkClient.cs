using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public interface IPublisherVkClient
{
    Task<(long Id, string Name)> CheckAsync(string address, string secret, CancellationToken token);
    Task<long> PostAsync(long community, string secret, string message, string guid, CancellationToken token);
}

/// <summary>No raw VK errors: request_params can echo the access token.</summary>
public sealed class PublisherVkClient(HttpClient http) : IPublisherVkClient
{
    public async Task<(long Id, string Name)> CheckAsync(string address, string secret, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 4096 || secret.Any(char.IsWhiteSpace))
            throw new PublisherException("Publisher.InvalidToken");
        var name = ParseCommunity(address);
        long id;
        if (long.TryParse(name, out var numeric)) id = numeric;
        else
        {
            using var resolved = await CallAsync("utils.resolveScreenName", secret, new() { ["screen_name"] = name }, token);
            var data = resolved.RootElement.GetProperty("response");
            if (data.ValueKind != JsonValueKind.Object || data.GetProperty("type").GetString() is not ("group" or "page"))
                throw new PublisherException("Publisher.InvalidCommunity");
            id = data.GetProperty("object_id").GetInt64();
        }
        if (id <= 0) throw new PublisherException("Publisher.InvalidCommunity");
        using var groups = await CallAsync("groups.getById", secret, new() { ["group_ids"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, token);
        var response = groups.RootElement.GetProperty("response");
        var list = response.ValueKind == JsonValueKind.Array ? response : response.GetProperty("groups");
        if (list.GetArrayLength() != 1 || list[0].GetProperty("id").GetInt64() != id)
            throw new PublisherException("Publisher.InvalidCommunity");
        try
        {
            using var permissions = await CallAsync("groups.getTokenPermissions", secret, new(), token);
            if (!permissions.RootElement.GetProperty("response").GetProperty("permissions").EnumerateArray()
                .Any(p => p.GetProperty("name").GetString() == "wall" && p.GetProperty("setting").GetInt32() > 0))
                throw new PublisherException("Publisher.VkPermission");
            // With a community token and no explicit IDs, this identifies the token's own group.
            using var ownGroup = await CallAsync("groups.getById", secret, new(), token);
            var ownResponse = ownGroup.RootElement.GetProperty("response");
            var ownList = ownResponse.ValueKind == JsonValueKind.Array ? ownResponse : ownResponse.GetProperty("groups");
            if (ownList.GetArrayLength() != 1 || ownList[0].GetProperty("id").GetInt64() != id)
                throw new PublisherException("Publisher.CommunityTokenMismatch");
        }
        catch (PublisherException error) when (error.ApiCode is 5 or 27)
        {
            // A user token must actually have wall permission and community management access.
            using var permissions = await CallAsync("account.getAppPermissions", secret, new(), token);
            if ((permissions.RootElement.GetProperty("response").GetInt64() & 8192) == 0
                || !list[0].TryGetProperty("is_admin", out var admin) || admin.GetInt32() != 1)
                throw new PublisherException("Publisher.VkPermission");
        }
        // Read access is deliberately not advertised as proof that wall.post will succeed.
        return (id, list[0].GetProperty("name").GetString() ?? name);
    }

    public static string ParseCommunity(string address)
    {
        var value = address.Trim();
        if (long.TryParse(value, out var id) && id > 0) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host is not ("vk.ru" or "vk.com") || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new PublisherException("Publisher.InvalidCommunity");
        var path = uri.AbsolutePath.Trim('/');
        if (!Regex.IsMatch(path, @"\A[A-Za-z0-9_.]{1,100}\z")) throw new PublisherException("Publisher.InvalidCommunity");
        var match = Regex.Match(path, @"\A(?:club|public)([1-9][0-9]*)\z");
        return match.Success ? match.Groups[1].Value : path;
    }

    public async Task<long> PostAsync(long community, string secret, string message, string guid, CancellationToken token)
    {
        using var result = await CallAsync("wall.post", secret, new()
        {
            ["owner_id"] = (-community).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["from_group"] = "1", ["message"] = message, ["guid"] = guid
        }, token);
        if (!result.RootElement.GetProperty("response").TryGetProperty("post_id", out var post) || post.GetInt64() <= 0)
            throw new PublisherException("Publisher.InvalidResponse");
        return post.GetInt64();
    }

    private async Task<JsonDocument> CallAsync(string method, string secret, Dictionary<string, string> data, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(35) : http.Timeout);
        data["access_token"] = secret; data["v"] = "5.199";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.vk.com/method/" + method)
        { Content = new FormUrlEncodedContent(data) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new PublisherException("Publisher.NetworkError");
        await response.Content.LoadIntoBufferAsync(1024 * 1024, deadline.Token);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.GetProperty("error_code").GetInt32(); json.Dispose();
            throw new PublisherException(code switch
            { 5 => "Publisher.InvalidToken", 6 or 9 or 29 => "Publisher.RateLimit", 7 or 15 or 27 or 214 => "Publisher.VkPermission", _ => "Publisher.VkError" }, code);
        }
        return json;
    }
}
