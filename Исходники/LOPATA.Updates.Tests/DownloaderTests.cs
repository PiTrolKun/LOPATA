using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class DownloaderTests
{
    [TestMethod]
    public async Task InterruptedDownloadResumesAndVerifiedPackageIsNotDownloadedAgain()
    {
        using var fixture = new UpdateFixture();
        var bytes = new byte[65536]; Random.Shared.NextBytes(bytes);
        var manifest = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "data"));
        manifest = manifest with { Packages = [manifest.Packages[0] with { Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }] };
        var cache = Path.Combine(fixture.Folder, "cache"); Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(cache, "files.zip.part"), bytes[..4096]);
        using var handler = new PackageHandler(bytes);
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, cache);
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.AreEqual(4096L, handler.LastStart);
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, bytes.Length, manifest.Packages[0].Sha256));
        await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task IncorrectRangePreservesPrefixAndBadChecksumCannotBecomeReady()
    {
        using var fixture = new UpdateFixture();
        var bytes = new byte[8192];
        var manifest = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "data"));
        manifest = manifest with { Packages = [manifest.Packages[0] with { Size = bytes.Length }] };
        var cache = Path.Combine(fixture.Folder, "cache"); Directory.CreateDirectory(cache);
        var partial = Path.Combine(cache, "files.zip.part");
        await File.WriteAllBytesAsync(partial, bytes[..4096]);
        using var handler = new PackageHandler(bytes) { WrongRange = true };
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, cache);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0]));
        Assert.AreEqual(4096L, new FileInfo(partial).Length);
        handler.WrongRange = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0]));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "files.zip")));
        Assert.IsFalse(File.Exists(partial));
    }

    private sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
    {
        public long LastStart; public int Calls; public bool WrongRange;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            LastStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var result = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes[(int)LastStart..]) };
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(WrongRange ? 0 : LastStart, bytes.Length - 1, bytes.Length);
            return Task.FromResult(result);
        }
    }
}
