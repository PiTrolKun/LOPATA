using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

/// <summary>Authenticated upstream hashes, sealed to this Windows user; never stores an access token.</summary>
internal static class VideoLtxMetadata
{
    internal sealed record FileHash(string Path, long Bytes, string Sha256);
    internal sealed record Receipt(string Repository, string Revision, FileHash[] Files);
    internal static string CachePath(ManagedModelArtifactCard card) => Path.Combine(card.InstallDirectory, ".source-metadata.dpapi");
    internal static bool Complete(ManagedModelArtifactCard card) => card.Files.All(f => IsHash(f.Sha256));
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool Load(ManagedModelArtifactCard card)
    {
        var path = CachePath(card);
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 64 * 1024) return false;
            var json = PublisherSecretProtection.Unprotect(File.ReadAllText(path));
            var receipt = JsonSerializer.Deserialize<Receipt>(json);
            ValidateAndApply(card, receipt);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
            or FormatException or JsonException or InvalidDataException)
        {
            // A broken/untrusted cache cannot supply hashes; explicit access verification can replace it.
            foreach (var file in card.Files) file.Sha256 = "";
            return false;
        }
    }

    internal static async Task ResolveAsync(ManagedModelArtifactCard card, HttpClient http, CancellationToken token)
    {
        var url = "https://huggingface.co/api/models/" + card.RepositoryId + "/tree/" + card.Revision + "?recursive=true&expand=false";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var bounded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                if (bounded.Length + read > 1024 * 1024) throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
                await bounded.WriteAsync(buffer.AsMemory(0, read), token);
            }
            using var document = JsonDocument.Parse(bounded.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
            var hashes = new List<FileHash>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) continue;
                var name = path.GetString()!;
                if (!card.Files.Any(f => f.RelativePath == name)) continue;
                if (!item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes)
                    || !item.TryGetProperty("lfs", out var lfs) || lfs.ValueKind != JsonValueKind.Object
                    || !lfs.TryGetProperty("oid", out var oid) || oid.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
                hashes.Add(new(name, bytes, oid.GetString()!));
            }
            var receipt = new Receipt(card.RepositoryId, card.Revision, hashes.ToArray());
            ValidateAndApply(card, receipt);
            token.ThrowIfCancellationRequested();
            Save(card, receipt, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException error) when (error.Message == "VideoPrep.LtxAccessRequired") { throw; }
        catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or InvalidOperationException)
        { throw new InvalidDataException("VideoPrep.LtxMetadataInvalid"); }
        catch (HttpRequestException)
        { throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable"); }
    }

    private static void ValidateAndApply(ManagedModelArtifactCard card, Receipt? receipt)
    {
        if (receipt is null || receipt.Repository != card.RepositoryId || receipt.Revision != card.Revision
            || receipt.Files is null || receipt.Files.Length != card.Files.Count
            || receipt.Files.Any(f => f is null) || receipt.Files.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != card.Files.Count)
            throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
        foreach (var expected in card.Files)
        {
            var found = receipt.Files.SingleOrDefault(f => f.Path == expected.RelativePath);
            if (found is null || found.Bytes != expected.SizeBytes || !IsHash(found.Sha256))
                throw new InvalidDataException("VideoPrep.LtxMetadataInvalid");
        }
        foreach (var expected in card.Files)
            expected.Sha256 = receipt.Files.Single(f => f.Path == expected.RelativePath).Sha256.ToLowerInvariant();
    }

    private static void Save(ManagedModelArtifactCard card, Receipt receipt, CancellationToken token)
    {
        var path = CachePath(card);
        var encrypted = PublisherSecretProtection.Protect(JsonSerializer.Serialize(receipt));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(card.InstallDirectory);
        try
        {
            File.WriteAllText(temporary, encrypted);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Scopes bearer authentication to HF and follows redirects without forwarding credentials.</summary>
internal sealed class VideoLtxTransport : HttpMessageHandler
{
    private readonly HttpClient _inner;
    private readonly bool _ownsClient;
    private readonly Func<string> _accessToken;
    internal VideoLtxTransport(Func<string> accessToken, HttpClient? inner = null)
    {
        if (inner?.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
        _accessToken = accessToken; _ownsClient = inner is null;
        _inner = inner ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri ?? throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
        for (var redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)
                throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
            using var forwarded = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers)
                if (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var secret = _accessToken();
            if (uri.IdnHost.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) && secret.Length > 0)
                forwarded.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            HttpResponseMessage response;
            try { response = await _inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) { throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable"); }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new InvalidOperationException("VideoPrep.LtxAccessRequired");
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/models/", StringComparison.Ordinal)
                && !uri.IdnHost.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
        }
        throw new HttpRequestException("VideoPrep.LtxMetadataUnavailable");
    }
    protected override void Dispose(bool disposing)
    { if (disposing && _ownsClient) _inner.Dispose(); base.Dispose(disposing); }
}
