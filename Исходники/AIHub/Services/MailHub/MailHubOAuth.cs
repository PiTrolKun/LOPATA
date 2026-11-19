using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.MailHub;

internal static class MailHubOAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private sealed record Endpoints(string Authorize, string Token, string Scope);

    private static Endpoints For(MailHubProvider provider) => provider switch
    {
        MailHubProvider.Gmail => new("https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token", "https://mail.google.com/"),
        MailHubProvider.Outlook => new("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            "https://outlook.office.com/IMAP.AccessAsUser.All offline_access"),
        _ => throw new MailHubInputException("MailHub.BadAuthentication")
    };

    internal static (string Id, string Secret) PublicClient(MailHubProvider provider)
    {
        string prefix = provider == MailHubProvider.Gmail ? "LOPATA_GMAIL" : "LOPATA_OUTLOOK";
        return (Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID") ?? "",
            provider == MailHubProvider.Gmail ? Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET") ?? "" : "");
    }

    internal static async Task<MailHubSecret> AuthorizeAsync(MailHubAccount account, string clientId,
        string clientSecret, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new MailHubInputException("MailHub.OAuthClientRequired");
        var endpoints = For(account.Provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(4));
        token = lifetime.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 4);
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/mail-hub/callback";
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Dictionary<string, string> parameters = new()
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = endpoints.Scope, ["state"] = state, ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256", ["login_hint"] = account.Address
        };
        if (account.Provider == MailHubProvider.Gmail)
        {
            parameters["access_type"] = "offline";
            parameters["prompt"] = "consent";
        }
        var browse = endpoints.Authorize + "?" + string.Join("&",
            parameters.Select(parameter => Uri.EscapeDataString(parameter.Key) + "=" + Uri.EscapeDataString(parameter.Value)));
        using var browser = Process.Start(new ProcessStartInfo(browse) { UseShellExecute = true });
        var code = await ReadCallbackAsync(listener, state, token);
        var current = new MailHubSecret(ClientId: clientId, ClientSecret: clientSecret);
        return await RedeemAsync(endpoints, current, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier
        }, token);
    }

    internal static Task<MailHubSecret> RefreshAsync(MailHubProvider provider, MailHubSecret current, CancellationToken token)
    {
        if (string.IsNullOrEmpty(current.RefreshToken)) throw new MailHubInputException("MailHub.Reconnect");
        return current.Expires > DateTimeOffset.UtcNow.AddMinutes(2) && !string.IsNullOrEmpty(current.AccessToken)
            ? Task.FromResult(current)
            : RedeemAsync(For(provider), current, new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken
            }, token);
    }

    private static async Task<MailHubSecret> RedeemAsync(Endpoints endpoints, MailHubSecret current,
        Dictionary<string, string> parameters, CancellationToken token)
    {
        parameters["client_id"] = current.ClientId;
        if (!string.IsNullOrWhiteSpace(current.ClientSecret)) parameters["client_secret"] = current.ClientSecret;
        using var payload = new FormUrlEncodedContent(parameters);
        using var response = await Http.PostAsync(endpoints.Token, payload, token);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new MailHubInputException("MailHub.ProviderUnavailable");
        if (!response.IsSuccessStatusCode) throw new MailHubInputException("MailHub.Reconnect");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var accessValue) ? accessValue.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : current.RefreshToken;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new MailHubInputException("MailHub.Reconnect");
        int seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var count) ? count : 3600;
        return current with { AccessToken = access, RefreshToken = refresh, Expires = DateTimeOffset.UtcNow.AddSeconds(seconds) };
    }

    private static async Task<string> ReadCallbackAsync(TcpListener listener, string state, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var connection = client.GetStream();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(3));
            var bytes = new byte[8192];
            var length = 0;
            int end = -1;
            try
            {
                while (length < bytes.Length && end < 0)
                {
                    int received = await connection.ReadAsync(bytes.AsMemory(length), request.Token);
                    if (received == 0) break;
                    length += received;
                    end = bytes.AsSpan(0, length).IndexOf((byte)'\n');
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            catch (IOException) { continue; }
            if (end < 0) continue;
            var line = Encoding.ASCII.GetString(bytes, 0, end).TrimEnd('\r').Split(' ');
            if (line is not ["GET", var target, _] || !target.StartsWith("/mail-hub/callback?", StringComparison.Ordinal))
                continue;
            var query = System.Web.HttpUtility.ParseQueryString(target[(target.IndexOf('?') + 1)..]);
            if (!string.Equals(query["state"], state, StringComparison.Ordinal)) continue;
            var code = query["code"];
            const string message = "LOPATA Mail Hub. Return to the application.";
            var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {message.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{message}");
            try { await connection.WriteAsync(reply, request.Token); }
            catch (IOException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return !string.IsNullOrWhiteSpace(code) ? code : throw new MailHubInputException("MailHub.Reconnect");
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
