using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ModelDownloadRestartTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 32768).Select(i => (byte)(i * 23)).ToArray();
    private sealed class Network(string mode) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; var data = mode == "bad-hash" ? new byte[Payload.Length] : mode == "short" ? Payload[..100] : Payload;
            var response = new HttpResponseMessage(mode is "valid-range" or "wrong-range" ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            if (mode is "valid-range" or "wrong-range")
            {
                var from = mode == "wrong-range" ? 0 : request.Headers.Range!.Ranges.Single().From!.Value;
                response.Content = new ByteArrayContent(Payload[(int)from..]);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
            }
            else response.Content = new ByteArrayContent(data);
            if (mode == "short") response.Content.Headers.ContentLength = Payload.Length;
            return Task.FromResult(response);
        }
    }
    private sealed class Progress(Action<ManagedModelDownloadProgress> action) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress p) => action(p); }
    private static ManagedModelArtifactCard Card(string root) => new()
    {
        ModelArtifactId = "test-no-range", DisplayName = "test", Revision = "pin", IsManaged = true, Role = ManagedModelRoles.Tool,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "test"),
        Files = [new() { RelativePath = "source.zip", SizeBytes = Payload.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Payload)), SourceUrl = "https://test.invalid/source.zip" }]
    };
    [TestMethod, DataRow(1), DataRow(4)]
    public async Task Http200RestartsOneFileWithoutAppendingTheFullResponseToItsPrefix(int connections)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var store = new ManagedModelLibraryStore(f.File("library")); store.Upsert(card); Directory.CreateDirectory(card.InstallDirectory);
        var target = Path.Combine(card.InstallDirectory, "source.zip"); var prefix = Payload[..1024]; await File.WriteAllBytesAsync(target + ".part", prefix);
        var keep = Path.Combine(card.InstallDirectory, "keep.bin"); await File.WriteAllBytesAsync(keep, [1, 9, 7]);
        var network = new Network("full"); using var http = new HttpClient(network);
        using var acquisition = new ManagedModelAcquisitionService(store, http, 256) { MaximumParallelConnections = connections };
        var stages = new List<string>(); var checkedPrefix = false;
        var result = await acquisition.DownloadAsync(card.ModelArtifactId, new Progress(p =>
        {
            stages.Add(p.Stage);
            if (p.Stage == "restarting") { CollectionAssert.AreEqual(prefix, File.ReadAllBytes(target + ".part")); checkedPrefix = true; }
        }), default);
        Assert.IsTrue(checkedPrefix); Assert.AreEqual(ManagedModelStatuses.Installed, result.Status); Assert.Contains("restarting", stages);
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(target)); CollectionAssert.AreEqual(new byte[] { 1, 9, 7 }, await File.ReadAllBytesAsync(keep));
        Assert.IsFalse(Directory.EnumerateFiles(card.InstallDirectory, "*.restart-*").Any());
        Assert.AreEqual(connections == 1 ? 1 : 2, network.Calls);
    }
    [TestMethod, DataRow("bad-hash"), DataRow("short"), DataRow("cancel"), DataRow("wrong-range")]
    public async Task FailedOrCancelledReplacementPreservesOldPartial(string mode)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); Directory.CreateDirectory(card.InstallDirectory); var target = Path.Combine(card.InstallDirectory, "source.zip");
        var prefix = Payload[..1024]; await File.WriteAllBytesAsync(target + ".part", prefix);
        var network = new Network(mode); using var http = new HttpClient(network); using var cancel = new CancellationTokenSource();
        var loader = new SegmentedModelFileDownloader(http) { MaximumParallelConnections = 1 };
        var progress = new Progress(p => { if (mode == "cancel" && p.Stage == "restarting") cancel.Cancel(); });
        if (mode == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => loader.DownloadAsync(card, card.Files[0], target, 0, progress, cancel.Token));
        else if (mode is "bad-hash" or "wrong-range")
            await Assert.ThrowsAsync<InvalidDataException>(() => loader.DownloadAsync(card, card.Files[0], target, 0, progress, cancel.Token));
        else await Assert.ThrowsAsync<EndOfStreamException>(() => loader.DownloadAsync(card, card.Files[0], target, 0, progress, cancel.Token));
        CollectionAssert.AreEqual(prefix, await File.ReadAllBytesAsync(target + ".part")); Assert.IsFalse(File.Exists(target));
        Assert.IsFalse(Directory.EnumerateFiles(card.InstallDirectory, "*.restart-*").Any());
    }
    [TestMethod]
    public async Task SupportedSequentialRangeStillResumesWithoutRestart()
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models")); Directory.CreateDirectory(card.InstallDirectory);
        var target = Path.Combine(card.InstallDirectory, "source.zip"); await File.WriteAllBytesAsync(target + ".part", Payload[..1024]);
        var network = new Network("valid-range"); using var http = new HttpClient(network);
        var loader = new SegmentedModelFileDownloader(http) { MaximumParallelConnections = 1 };
        var stages = new List<string>(); await loader.DownloadAsync(card, card.Files[0], target, 0, new Progress(p => stages.Add(p.Stage)), default);
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(target + ".part")); Assert.DoesNotContain("restarting", stages); Assert.AreEqual(1, network.Calls);
    }
    [TestMethod]
    public void RestartHasAnExplicitStateAndDoesNotCountOldPrefixAsNewlyReceivedBytes()
    {
        var state = new VideoH3DownloadState([Card("test")]);
        state.Refresh([new("test-no-range", "source.zip", 1024, false)]);
        state.Apply(new("test-no-range", "source.zip", 0, Payload.Length, 0, "restarting"));
        Assert.AreEqual("restarting", state.Rows[0].Stage); Assert.AreEqual(0L, state.StoredBytes); Assert.AreEqual(0d, state.BytesPerSecond);
        state.Refresh([new("test-no-range", "source.zip", 1024, false)]); Assert.AreEqual(1024L, state.StoredBytes); Assert.AreEqual("partial", state.Rows[0].Stage);
    }
}
