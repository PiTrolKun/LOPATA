using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class ParallelDownloadTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(4)] [DataRow(8)] [DataRow(0)]
    public async Task MixedPackagesAndRangesShareOneConnectionLimit(int connections)
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(11, 16384);
        using var handler = new ParallelDownloadHandler(payloads);
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, Path.Combine(fixture.Folder, "cache"), 4096)
            { MaximumParallelConnections = connections };
        var paths = await downloader.DownloadManyAsync(manifest, manifest.Packages);
        foreach (var package in manifest.Packages)
            Assert.IsTrue(await UpdatePlanner.MatchesAsync(paths[package.Id], package.Size, package.Sha256));
        Assert.AreEqual(connections == 0 ? 4 : connections, handler.Peak);
        Assert.AreEqual(0, handler.Active);
        var calls = handler.Requests.Count;
        await downloader.DownloadManyAsync(manifest, manifest.Packages);
        Assert.AreEqual(calls, handler.Requests.Count, "Verified cache must not use the network.");
    }

    [TestMethod]
    public async Task LargePackageAloneUsesRangesAndAcceptsOldSequentialPrefix()
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(1, 65536);
        var cache = Path.Combine(fixture.Folder, "cache"); Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(cache, "file0.zip.part"), payloads["file0.zip"][..6000]);
        using var handler = new ParallelDownloadHandler(payloads);
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, cache, 4096) { MaximumParallelConnections = 4 };
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.AreEqual(4, handler.Peak);
        Assert.IsTrue(handler.Requests.Any(r => r.From == 6000 && r.To == 8191));
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, 65536, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    public async Task CancelledRangesResumeWithChangedConnectionLimit()
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(1, 131072);
        var cache = Path.Combine(fixture.Folder, "cache");
        using var handler = new ParallelDownloadHandler(payloads);
        using var http = new HttpClient(handler);
        using var stop = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.StoredBytes >= 8192 && p.Stage == "downloading") stop.Cancel(); });
        var downloader = new UpdatePackageDownloader(http, cache, 4096) { MaximumParallelConnections = 4 };
        await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0], progress, stop.Token));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "file0.zip")));
        Assert.AreEqual(0, handler.Active);
        var received = handler.Received;
        handler.Peak = 0; downloader.MaximumParallelConnections = 1;
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.AreEqual(1, handler.Peak);
        Assert.IsTrue(handler.Received - received < 131072, "The resumed request must reuse saved ranges.");
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, 131072, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    public async Task InterruptedRangesKeepBytesAndResumeAfterConnectionFailure()
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(1, 65536);
        using var handler = new ParallelDownloadHandler(payloads) { BreakStream = true };
        using var http = new HttpClient(handler);
        var cache = Path.Combine(fixture.Folder, "cache");
        var downloader = new UpdatePackageDownloader(http, cache, 4096) { MaximumParallelConnections = 2 };
        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0]));
        var saved = Directory.GetFiles(Path.Combine(cache, "file0.zip.part.ranges")).Sum(p => new FileInfo(p).Length);
        Assert.IsTrue(saved > 0);
        handler.BreakStream = false;
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.IsTrue(handler.Requests.Any(r => r.From > 0 && r.From % 4096 != 0));
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, 65536, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    public async Task ServerWithoutRangesFallsBackAndStillHonorsBatchBudget()
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(3, 16384);
        using var handler = new ParallelDownloadHandler(payloads) { NoRanges = true };
        using var http = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(http, Path.Combine(fixture.Folder, "cache"), 4096)
            { MaximumParallelConnections = 2 };
        var paths = await downloader.DownloadManyAsync(manifest, manifest.Packages);
        Assert.AreEqual(2, handler.Peak);
        foreach (var package in manifest.Packages)
            Assert.IsTrue(await UpdatePlanner.MatchesAsync(paths[package.Id], package.Size, package.Sha256));
    }

    [TestMethod]
    public async Task WrongRangesAndCorruptAssemblyCannotBecomeReady()
    {
        using var fixture = new UpdateFixture();
        var (manifest, payloads) = Data(1, 16384);
        using var handler = new ParallelDownloadHandler(payloads) { WrongRange = true };
        using var http = new HttpClient(handler);
        var cache = Path.Combine(fixture.Folder, "cache");
        var downloader = new UpdatePackageDownloader(http, cache, 4096) { MaximumParallelConnections = 4 };
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0]));
        handler.WrongRange = false; handler.Corrupt = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(manifest, manifest.Packages[0]));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "file0.zip")));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "file0.zip.part")));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(cache, "file0.zip.part.ranges")).Length);
        handler.Corrupt = false;
        var path = await downloader.DownloadAsync(manifest, manifest.Packages[0]);
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(path, 16384, manifest.Packages[0].Sha256));
    }

    [TestMethod]
    [DataRow("{\"modelDownloads\":{\"maximumParallelConnections\":8}}", 8)]
    [DataRow("{\"modelDownloads\":{\"maximumParallelConnections\":2}}", 2)]
    [DataRow("{\"modelDownloads\":{\"maximumParallelConnections\":99}}", 0)]
    [DataRow("{\"modelDownloads\":{\"maximumParallelConnections\":\"8\"}}", 0)]
    [DataRow("{}", 0)] [DataRow("null", 0)] [DataRow("broken", 0)]
    public void StandaloneReadsSharedSettingWithoutRewritingIt(string json, int expected)
    {
        using var fixture = new UpdateFixture();
        var path = Path.Combine(fixture.Folder, "settings.json");
        Assert.AreEqual(0, UpdateDownloadSettings.Read(path));
        File.WriteAllText(path, json);
        Assert.AreEqual(expected, UpdateDownloadSettings.Read(path));
        Assert.AreEqual(json, File.ReadAllText(path));
    }

    private static (UpdateManifest, Dictionary<string, byte[]>) Data(int count, int size)
    {
        var payloads = Enumerable.Range(0, count).ToDictionary(i => $"file{i}.zip", i => RandomNumberGenerator.GetBytes(i % 2 == 0 ? size : 1024));
        var manifest = UpdateFixture.Manifest("0.2.43-beta", payloads.Select((p, i) => UpdateFixture.Entry($"file{i}.dll", "fixture", p.Key)).ToArray());
        manifest = manifest with { Packages = manifest.Packages.Select(p => p with { Size = payloads[p.Id].Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(payloads[p.Id])) }).ToArray() };
        return (manifest, payloads);
    }
    private sealed class InlineProgress(Action<UpdateTransferProgress> report) : IProgress<UpdateTransferProgress>
    { public void Report(UpdateTransferProgress value) => report(value); }
}
