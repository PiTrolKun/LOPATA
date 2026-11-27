using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.VisualConstructor;

internal enum ResearchImportMode { Overview, Selected, Snapshot }

internal sealed record ResearchImport(string Address, string Ref, ResearchImportMode Mode,
    string[] Paths, string[] Extensions, int MaxMiB, int MaxFileKiB);

internal sealed class ConstructorResearchRepository(HttpClient http)
{
    private const int MaxFiles = 45;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<ResearchMaterial> ReadAsync(ResearchImport input, CancellationToken token)
    {
        var repository = PublisherGitHubClient.ParseRepository(input.Address);
        if (string.IsNullOrEmpty(repository)) throw new InvalidDataException("VisualConstructor.ResearchRepositoryInvalid");
        if (input.MaxMiB is < 1 or > 128 || input.MaxFileKiB is < 1 or > 2048)
            throw new InvalidDataException("VisualConstructor.ResearchImportLimits");
        if (input.Mode == ResearchImportMode.Selected && input.Paths.Length == 0
            || input.Mode == ResearchImportMode.Snapshot && input.Extensions.Length == 0)
            throw new InvalidDataException("VisualConstructor.ResearchChooseFiles");
        var root = $"https://api.github.com/repos/{repository}";
        using var meta = await ReadAsync(root, 1024 * 1024, token);
        var details = meta.RootElement;
        var reference = string.IsNullOrWhiteSpace(input.Ref) ? details.GetProperty("default_branch").GetString()! : input.Ref;
        using var revision = await ReadAsync(root + "/commits/" + Uri.EscapeDataString(reference), 4 * 1024 * 1024, token);
        var commit = revision.RootElement;
        var hash = ReadHash(commit);
        var source = ReadText(commit.GetProperty("commit"), "message");
        var license = await ReadLicenseAsync(root, hash, token);
        var summary = (ReadText(details, "description") + "\n\n" + source).Trim();
        var result = new ResearchMaterial(Guid.NewGuid(), repository, $"https://github.com/{repository}/tree/{hash}",
            DateTimeOffset.UtcNow, hash, license, summary, [], []);
        if (input.Mode == ResearchImportMode.Overview) return result;

        var tree = ReadHash(commit.GetProperty("commit").GetProperty("tree"));
        using var snapshot = await ReadAsync(root + $"/git/trees/{tree}?recursive=1", 10 * 1024 * 1024, token);
        var truncated = snapshot.RootElement.GetProperty("truncated").GetBoolean();
        var files = new List<ResearchFile>();
        var skipped = new List<ResearchSkip>();
        var paths = input.Paths.ToHashSet(StringComparer.Ordinal);
        var extensions = input.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long remaining = input.MaxMiB * ConstructorResearchArchive.MiB;
        var downloadAttempts = 0;
        foreach (var item in snapshot.RootElement.GetProperty("tree").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (ReadText(item, "type") == "tree") continue;
            var path = ReadText(item, "path");
            var selected = input.Mode == ResearchImportMode.Selected ? paths.Remove(path) : extensions.Contains(Path.GetExtension(path));
            if (!selected)
            {
                skipped.Add(new(path, "Filter"));
                continue;
            }
            if (ReadText(item, "mode") is not ("100644" or "100755")
                || !item.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size < 0)
            {
                skipped.Add(new(path, "Type"));
                continue;
            }
            if (downloadAttempts == MaxFiles || size > input.MaxFileKiB * 1024L || size > remaining)
            {
                skipped.Add(new(path, "Limit"));
                continue;
            }
            try
            {
                downloadAttempts++;
                var blob = ReadHash(item);
                using var content = await ReadAsync(root + "/git/blobs/" + blob, input.MaxFileKiB * 2048L + 4096L, token);
                var bytes = Convert.FromBase64String(content.RootElement.GetProperty("content").GetString()!);
                if (bytes.LongLength != size) throw new InvalidDataException("VisualConstructor.ResearchRepositoryInvalid");
                var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
                if (text.Contains('\0')) { skipped.Add(new(path, "Type")); continue; }
                files.Add(new(path, text));
                remaining -= size;
            }
            catch (DecoderFallbackException) { skipped.Add(new(path, "Type")); }
            catch (HttpRequestException error)
            {
                skipped.Add(new(path, "Unavailable"));
                if (error.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                    downloadAttempts = MaxFiles;
            }
        }
        skipped.AddRange(paths.Select(path => new ResearchSkip(path, "Missing")));
        return result with { Files = files.ToArray(), Skipped = skipped.ToArray(),
            Incomplete = truncated || skipped.Any(file => file.Reason != "Filter") };
    }

    private async Task<JsonDocument> ReadAsync(string address, long maximum, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("LOPATA-TechnicalResearch/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(maximum, deadline.Token);
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
    }

    private async Task<string> ReadLicenseAsync(string root, string revision, CancellationToken token)
    {
        try
        {
            using var license = await ReadAsync(root + $"/license?ref={revision}", 256 * 1024, token);
            return license.RootElement.TryGetProperty("license", out var information) ? ReadText(information, "spdx_id") : "";
        }
        catch (HttpRequestException) { return ""; }
    }

    private static string ReadText(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string ReadHash(JsonElement item)
    {
        var hash = ReadText(item, "sha");
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("VisualConstructor.ResearchRepositoryInvalid");
        return hash;
    }
}
