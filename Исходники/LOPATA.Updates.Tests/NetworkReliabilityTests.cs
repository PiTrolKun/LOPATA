using System.Net;
using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class NetworkReliabilityTests
{
    [TestMethod]
    public async Task StalledHeadersAreRetriedWithoutUnlimitedWaiting()
    {
        using var fixture = new UpdateFixture();
        var (manifest, bytes) = Data();
        using var handler = new StallHandler(bytes) { StallHeaders = true };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new UpdatePackageDownloader(http, Path.Combine(fixture.Folder, "cache"))
            { NetworkIdleTimeout = TimeSpan.FromMilliseconds(150) };
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, handler.Calls);
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, bytes.Length, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    public async Task StalledBodyResumesItsSavedPrefix()
    {
        using var fixture = new UpdateFixture();
        var (manifest, bytes) = Data();
        using var handler = new StallHandler(bytes);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new UpdatePackageDownloader(http, Path.Combine(fixture.Folder, "cache"))
            { NetworkIdleTimeout = TimeSpan.FromMilliseconds(150), MaximumParallelConnections = 1 };
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1024L, handler.ResumedFrom);
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, bytes.Length, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    public async Task PermanentStallFailsAfterThreeAttemptsAndKeepsCancellationDistinct()
    {
        using var fixture = new UpdateFixture();
        var (manifest, bytes) = Data();
        using var handler = new StallHandler(bytes) { StallHeaders = true, AlwaysStall = true };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new UpdatePackageDownloader(http, Path.Combine(fixture.Folder, "cache"))
            { NetworkIdleTimeout = TimeSpan.FromMilliseconds(100) };
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0])
            .WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.AreEqual(3, handler.Calls);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Folder, "cache", "fixture.zip")));
        using var stop = new CancellationTokenSource(50);
        var before = handler.Calls;
        await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0], token: stop.Token));
        Assert.AreEqual(before + 1, handler.Calls);
    }

    [TestMethod]
    public void BatchVerificationCannotMaskPendingDownloadsOrRetries()
    {
        var (manifest, _) = Data();
        var second = manifest.Packages[0] with { Id = "second.zip" };
        var batch = new UpdateBatchProgress([manifest.Packages[0], second]);
        Assert.AreEqual("downloading", batch.Accept(new("fixture.zip", 4096, 4096, "verifying")).Stage);
        Assert.AreEqual("retrying", batch.Accept(new("second.zip", 100, 4096, "retrying")).Stage);
        Assert.AreEqual("retrying", batch.Accept(new("fixture.zip", 4096, 4096, "verified")).Stage);
        Assert.AreEqual("verifying", batch.Accept(new("second.zip", 4096, 4096, "verifying")).Stage);
        Assert.AreEqual(8192L, batch.Accept(new("second.zip", 4096, 4096, "extracting")).Bytes);
    }

    private static (UpdateManifest, byte[]) Data()
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var manifest = UpdateFixture.Manifest("0.5.2-beta", UpdateFixture.Entry("test.dll", "fixture", "fixture.zip"));
        manifest = manifest with { Packages = [manifest.Packages[0] with { Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }] };
        return (manifest, bytes);
    }

    private sealed class StallHandler(byte[] bytes) : HttpMessageHandler
    {
        public bool StallHeaders, AlwaysStall;
        public int Calls;
        public long? ResumedFrom;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var stall = Interlocked.Increment(ref Calls) == 1 || AlwaysStall;
            if (stall && StallHeaders) await Task.Delay(Timeout.Infinite, token);
            var from = request.Headers.Range?.Ranges.Single().From ?? 0;
            ResumedFrom = from;
            var stream = new StallStream(bytes[(int)from..], stall && !StallHeaders);
            var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = bytes.Length - from;
            if (from > 0) response.Content.Headers.ContentRange = new(from, bytes.Length - 1, bytes.Length);
            return response;
        }
    }

    private sealed class StallStream(byte[] bytes, bool stall) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (stall && Position > 0) await Task.Delay(Timeout.Infinite, token);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], token);
        }
    }
}
