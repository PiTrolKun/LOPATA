using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoLtxPreparationTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 13)).ToArray();
    private static string Hash => Convert.ToHexStringLower(SHA256.HashData(Bytes));
    private static ManagedModelArtifactCard Card(string root) => new()
    {
        ModelArtifactId = "test-ltx", DisplayName = "Test LTX", IsManaged = true, Revision = VideoLtxCatalog.Revision,
        RepositoryId = VideoLtxCatalog.Repository, Role = ManagedModelRoles.Tool, Format = "pinned-inputs",
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "owned"),
        Files = [new() { RelativePath = "model.bin", SizeBytes = Bytes.Length, Sha256 = "",
            SourceUrl = "https://huggingface.co/" + VideoLtxCatalog.Repository + "/resolve/" + VideoLtxCatalog.Revision + "/model.bin" }]
    };
    private sealed class Progress(Action<ManagedModelDownloadProgress> update) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress value) => update(value); }
    private sealed class Network : HttpMessageHandler
    {
        internal int MetadataCalls, PayloadCalls, Active, Peak;
        internal bool Fail, MaskHash, WrongSize, PayloadDenied;
        internal string? Json;
        internal HttpStatusCode MetadataStatus = HttpStatusCode.OK;
        internal CancellationTokenSource? CancelPayload;
        internal List<string?> Tokens = [];
        internal List<long> Starts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Fail) throw new HttpRequestException("Network must not be used.");
            lock (Tokens) Tokens.Add(request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.Contains("/api/models/", StringComparison.Ordinal))
            {
                MetadataCalls++;
                return new(MetadataStatus) { Content = new StringContent(Json ?? JsonSerializer.Serialize(new[]
                { new { path = "model.bin", size = WrongSize ? Bytes.Length + 1 : Bytes.Length,
                    lfs = new { oid = MaskHash ? new string('*', 64) : Hash } } })) };
            }
            var call = Interlocked.Increment(ref PayloadCalls);
            if (PayloadDenied) return new(HttpStatusCode.Forbidden) { Content = new StringContent("gated") };
            var active = Interlocked.Increment(ref Active);
            lock (Starts) Peak = Math.Max(Peak, active);
            try
            {
                if (call == 2) CancelPayload?.Cancel();
                await Task.Delay(20, token);
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range?.From is not { } from) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
                lock (Starts) Starts.Add(from);
                var end = range.To ?? Bytes.Length - 1;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(Bytes[(int)from..((int)end + 1)]) };
                response.Content.Headers.ContentRange = new(from, end, Bytes.Length);
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    [TestMethod]
    public void CatalogIsPinnedCompactAndDoesNotInventHashes()
    {
        using var f = new VideoPreparationTests.TestFiles();
        var cards = VideoLtxCatalog.Cards(f.File("models")); var card = cards.Single();
        Assert.AreEqual("video-ltx25-models", card.ModelArtifactId); Assert.AreEqual(5, card.Files.Count);
        Assert.AreEqual(39_689_918_812L, card.TotalBytes); Assert.AreEqual(VideoLtxCatalog.Revision, card.Revision);
        Assert.IsTrue(card.Files.All(x => x.Sha256 == "" && x.SourceUrl.Contains(card.Revision, StringComparison.Ordinal)));
        Assert.IsTrue(card.Files.Any(x => x.RelativePath.Contains("video-vae-conv-bf16", StringComparison.Ordinal)));
        Assert.IsFalse(card.Files.Any(x => x.RelativePath.Contains("enhancer", StringComparison.Ordinal)));
        Assert.Throws<InvalidOperationException>(() => VideoLtxCatalog.Cards("relative"));
    }

    [TestMethod]
    public async Task PageInspectionIsLocalAndExplicitDownloadRequiresAccess()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]);
        Assert.IsFalse(await service.PrepareAsync(false, null, default));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(true, null, default));
        Assert.AreEqual("VideoPrep.LtxAccessRequired", error.Message); Assert.AreEqual(0, network.MetadataCalls);
        Assert.IsFalse(Directory.Exists(card.InstallDirectory));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(false, null, cancel.Token));
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task BadMetadataCannotRegisterHashesOrStartPayload(bool masked)
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models"));
        var network = new Network { MaskHash = masked, WrongSize = !masked }; using var http = new HttpClient(network);
        using var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]) { AccessToken = "hf_test" };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(true, null, default));
        Assert.AreEqual("VideoPrep.LtxMetadataInvalid", error.Message); Assert.AreEqual(0, network.PayloadCalls);
        Assert.IsFalse(File.Exists(VideoLtxMetadata.CachePath(card))); Assert.IsTrue(card.Files.All(x => x.Sha256 == ""));
    }

    [TestMethod, DataRow("[null]"), DataRow("[4]"), DataRow("{}"), DataRow("[{\"path\":\"model.bin\",\"size\":\"wrong\",\"lfs\":{\"oid\":\"bad\"}}]")]
    public async Task MalformedMetadataGetsFriendlyKey(string json)
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models"));
        var network = new Network { Json = json }; using var http = new HttpClient(network);
        using var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]) { AccessToken = "hf_secret" };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(false, null, default));
        Assert.AreEqual("VideoPrep.LtxMetadataInvalid", error.Message); Assert.IsFalse(error.ToString().Contains("hf_secret", StringComparison.Ordinal));
    }

    [TestMethod, DataRow(401), DataRow(403)]
    public async Task DeniedAccessGetsFriendlyKeyAndWritesNothing(int status)
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models"));
        var network = new Network { MetadataStatus = (HttpStatusCode)status }; using var http = new HttpClient(network);
        using var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]) { AccessToken = "hf_secret" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(false, null, default));
        Assert.AreEqual("VideoPrep.LtxAccessRequired", error.Message); Assert.AreEqual(0, network.PayloadCalls);
        Assert.IsFalse(Directory.Exists(card.InstallDirectory));
    }

    [TestMethod]
    public async Task TrustedCacheIsSealedAndTamperingDoesNotSupplyHashes()
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models"));
        var network = new Network(); using var http = new HttpClient(network);
        using (var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]) { AccessToken = "hf_secret" })
            Assert.IsFalse(await service.PrepareAsync(false, null, default));
        var path = VideoLtxMetadata.CachePath(card); var encrypted = await File.ReadAllTextAsync(path);
        Assert.IsFalse(encrypted.Contains("hf_secret", StringComparison.Ordinal)); Assert.IsFalse(encrypted.Contains(Hash, StringComparison.Ordinal));
        network.Fail = true;
        using (var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]))
        { Assert.AreEqual(Hash, service.Cards.Single().Files.Single().Sha256); Assert.IsFalse(await service.PrepareAsync(false, null, default)); }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new VideoLtxMetadata.Receipt(card.RepositoryId, card.Revision,
            [new("model.bin", Bytes.Length, new string('a', 64))])));
        using var rejected = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card]);
        Assert.AreEqual("", rejected.Cards.Single().Files.Single().Sha256); Assert.IsFalse(await rejected.PrepareAsync(false, null, default));
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task DownloadUsesCommonResumeAndAuditContracts(bool enabled)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), enabled);
        var card = Card(f.File("models")); Directory.CreateDirectory(card.InstallDirectory);
        var path = Path.Combine(card.InstallDirectory, "model.bin"); await File.WriteAllBytesAsync(path + ".part", Bytes[..65536]);
        var network = new Network(); using var http = new HttpClient(network); var store = new ManagedModelLibraryStore(f.File("library"));
        using (var service = new VideoLtxPreparation(card.ModelsRoot, store, http, [card], 16384) { ParallelConnections = 4, AccessToken = "hf_secret" })
            Assert.IsTrue(await service.PrepareAsync(true, null, default));
        Assert.IsGreaterThan(1, network.Peak); Assert.IsTrue(network.Starts.All(n => n >= 65536));
        CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(path)); network.Fail = true;
        using var local = new VideoLtxPreparation(card.ModelsRoot, store, http, [card]); var stages = new List<string>();
        Assert.IsTrue(await local.PrepareAsync(false, new Progress(x => stages.Add(x.Stage)), default)); Assert.Contains("verified", stages);
        stages.Clear(); Assert.IsTrue(await local.PrepareAsync(false, new Progress(x => stages.Add(x.Stage)), default));
        Assert.Contains(enabled ? "verified" : "verifying-presence", stages);
        await File.WriteAllBytesAsync(path, new byte[Bytes.Length]); ComponentAudit.Invalidate(card.InstallDirectory);
        Assert.IsFalse(await local.PrepareAsync(false, null, default)); File.Delete(path);
        Assert.IsFalse(await local.PrepareAsync(false, null, default)); Assert.AreEqual(1, network.MetadataCalls);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(f.File("library"), "events.jsonl"))).Contains("hf_secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DownloadAccessExpiryUsesFriendlyKeyEvenWithCachedHashes()
    {
        using var f = new VideoPreparationTests.TestFiles(); var card = Card(f.File("models"));
        var network = new Network(); using var http = new HttpClient(network); var store = new ManagedModelLibraryStore(f.File("library"));
        using (var initial = new VideoLtxPreparation(card.ModelsRoot, store, http, [card]) { AccessToken = "hf_test" })
            Assert.IsFalse(await initial.PrepareAsync(false, null, default));
        network.PayloadDenied = true;
        using var service = new VideoLtxPreparation(card.ModelsRoot, store, http, [card]) { AccessToken = "hf_test" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(true, null, default));
        Assert.AreEqual("VideoPrep.LtxAccessRequired", error.Message); Assert.IsFalse(File.Exists(Path.Combine(card.InstallDirectory, "model.bin")));
    }

    private sealed class RedirectNetwork : HttpMessageHandler
    {
        internal List<(string Host, string? Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add((request.RequestUri!.Host, request.Headers.Authorization?.Parameter));
            if (Requests.Count != 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://cdn.example.test/signed-model"); return Task.FromResult(response);
        }
    }

    [TestMethod]
    public async Task PayloadRedirectDoesNotForwardBearerAndMetadataCannotLeaveHf()
    {
        var payload = new RedirectNetwork(); using var payloadHttp = new HttpClient(payload);
        using (var secured = new HttpClient(new VideoLtxTransport(() => "hf_secret", payloadHttp)))
        using (await secured.GetAsync("https://huggingface.co/a/b/resolve/pinned/model.bin"))
        { Assert.AreEqual("hf_secret", payload.Requests[0].Token); Assert.IsNull(payload.Requests[1].Token); }
        var metadata = new RedirectNetwork(); using var metadataHttp = new HttpClient(metadata);
        using var source = new HttpClient(new VideoLtxTransport(() => "hf_secret", metadataHttp));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetAsync("https://huggingface.co/api/models/a/b/tree/pinned"));
        Assert.AreEqual(1, metadata.Requests.Count);
    }

    [TestMethod]
    public async Task DeclinedTermsAndCancellationPreserveSourceAndPartial()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var network = new Network(); using var http = new HttpClient(network);
        using var service = new VideoLtxPreparation(card.ModelsRoot, new(f.File("library")), http, [card], 16384)
        { AccessToken = "hf_test", ParallelConnections = 4 };
        var gate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, ct) => throw new OperationCanceledException("Declined", ct);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, default));
            Assert.AreEqual(0, network.MetadataCalls); Assert.IsFalse(Directory.Exists(card.InstallDirectory));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
        Directory.CreateDirectory(card.InstallDirectory); var target = Path.Combine(card.InstallDirectory, "model.bin");
        await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
        var unrelated = Path.Combine(card.InstallDirectory, "user.txt"); await File.WriteAllTextAsync(unrelated, "keep");
        using var cancel = new CancellationTokenSource(); network.CancelPayload = cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, cancel.Token));
        Assert.IsTrue(File.Exists(target + ".part")); Assert.IsFalse(File.Exists(target)); Assert.AreEqual("keep", await File.ReadAllTextAsync(unrelated));
        network.CancelPayload = null;
        Assert.IsTrue(await service.PrepareAsync(true, null, default)); CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(target));
    }
}
