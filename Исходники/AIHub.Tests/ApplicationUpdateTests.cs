using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class ApplicationUpdateTests
{
    [TestMethod]
    public void VersionOrderingAndInvalidVersions()
    {
        var ordered = new[] { "0.1.9-dev", "0.1.9-beta", "0.1.9", "0.1.10-beta", "0.2.0-beta", "1.0.0" };
        for (var i = 1; i < ordered.Length; i++)
            Assert.IsTrue(ApplicationReleaseVersion.Parse(ordered[i])!.CompareTo(ApplicationReleaseVersion.Parse(ordered[i - 1])) > 0);
        foreach (var value in new[] { "", "../0.1.2", "0.1.2-beta.1", "0.1.2-alpha", "0.1", "999999999999.1.2" })
            Assert.IsNull(ApplicationReleaseVersion.Parse(value));
    }

    [TestMethod]
    public async Task CheckFiltersDevDraftBetaAndMissingAssets()
    {
        using var handler = new FeedHandler();
        using var http = new HttpClient(handler);
        var service = new ApplicationUpdateService(http, "unused");
        Assert.AreEqual("0.1.53-beta", (await service.CheckAsync("0.1.52", true, default))!.Version);
        Assert.IsNull(await service.CheckAsync("0.1.52", false, default));
        Assert.IsNull(await service.CheckAsync("0.1.53-beta", true, default));
        handler.Stable = true;
        Assert.AreEqual("0.1.53", (await service.CheckAsync("0.1.53-beta", false, default))!.Version);
        handler.BadHash = true;
        Assert.IsNull(await service.CheckAsync("0.1.52", true, default));
    }

    [TestMethod]
    public void OnlyExactRepositoryAssetUrlsAreAccepted()
    {
        var valid = "https://github.com/PiTrolKun/LOPATA/releases/download/v0.1.53-beta/LOPATA_Setup_0.1.53-beta.exe";
        Assert.IsTrue(ApplicationUpdateService.IsAssetUrl(valid, "v0.1.53-beta", "LOPATA_Setup_0.1.53-beta.exe"));
        foreach (var bad in new[] { valid.Replace("https:", "http:"), valid.Replace("LOPATA/releases", "OTHER/releases"),
                     valid + "?redirect=1", valid.Replace("github.com", "github.com.evil.test"), valid.Replace("github.com", "user@github.com") })
            Assert.IsFalse(ApplicationUpdateService.IsAssetUrl(bad, "v0.1.53-beta", "LOPATA_Setup_0.1.53-beta.exe"));
    }

    [TestMethod]
    public async Task SharedDownloaderUsesParallelRangesAndResumesThenVerifiesCache()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = new byte[129 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            using var handler = new ArtifactHandler(bytes);
            using var http = new HttpClient(handler);
            var update = Artifact(bytes);
            var destination = Path.Combine(folder, update.Version, update.Sha256.ToLowerInvariant(), update.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination + ".part", bytes[..4096]);
            var service = new ApplicationUpdateService(http, folder);
            var path = await service.DownloadAsync(update, 4, null, default);
            Assert.IsTrue(await ApplicationUpdateService.VerifyAsync(path, update, default));
            Assert.IsTrue(handler.MaximumConcurrent > 1, "Existing segmented downloader must run parallel requests.");
            Assert.IsTrue(handler.Starts.All(start => start >= 4096), "Stored prefix must not be downloaded again.");
            var requests = handler.Starts.Count;
            Assert.AreEqual(path, await service.DownloadAsync(update, 4, null, default));
            Assert.AreEqual(requests, handler.Starts.Count, "Verified cached installer should not be downloaded again.");
        }
        finally { Directory.Delete(folder, true); }
    }

    [TestMethod]
    public async Task CorruptedDownloadCannotBecomeAnInstaller()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lopata-update-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = new byte[8192];
            var update = Artifact(bytes) with { Sha256 = new string('a', 64) };
            using var http = new HttpClient(new ArtifactHandler(bytes));
            var failed = false;
            try { await new ApplicationUpdateService(http, folder).DownloadAsync(update, 1, null, default); }
            catch (InvalidDataException) { failed = true; }
            Assert.IsTrue(failed);
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).Length);
            Assert.AreEqual(0, Directory.GetFiles(folder, "*.part", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static ApplicationUpdate Artifact(byte[] bytes) => new("0.1.53-beta", "LOPATA_Setup_0.1.53-beta.exe",
        "https://github.com/PiTrolKun/LOPATA/releases/download/v0.1.53-beta/LOPATA_Setup_0.1.53-beta.exe",
        bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "test");

    private sealed class ArtifactHandler(byte[] bytes) : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<long> Starts = [];
        private int _active;
        public int MaximumConcurrent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            lock (Starts) MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            try
            {
                await Task.Delay(30, token);
                var range = request.Headers.Range?.Ranges.FirstOrDefault();
                var start = (int)(range?.From ?? 0);
                var end = (int)(range?.To ?? bytes.Length - 1);
                Starts.Add(start);
                var result = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes, start, end - start + 1) };
                if (range is not null) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
                return result;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public bool Stable;
        public bool BadHash;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var version = Stable ? "0.1.53" : "0.1.53-beta";
            var file = $"LOPATA_Setup_{version}.exe";
            var prefix = $"https://github.com/PiTrolKun/LOPATA/releases/download/v{version}/";
            var payload = request.RequestUri!.Host == "api.github.com"
                ? JsonSerializer.Serialize(new object[] {
                    new { tag_name = "v9.0.0", draft = true, prerelease = false },
                    new { tag_name = "v8.0.0-dev", draft = false, prerelease = false },
                    new { tag_name = "v7.0.0", draft = false, prerelease = false, assets = Array.Empty<object>() },
                    new { tag_name = "v" + version, draft = false, prerelease = !Stable, body = "notes", assets = new[] {
                        new { name = file, size = 100, browser_download_url = prefix + file },
                        new { name = "lopata-update.json", size = 100, browser_download_url = prefix + "lopata-update.json" } } } })
                : JsonSerializer.Serialize(new { version, fileName = file, size = 100, sha256 = BadHash ? "invalid" : new string('a', 64) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
