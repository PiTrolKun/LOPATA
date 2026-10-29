using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3PreparationTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 17)).ToArray();
    private static ManagedModelArtifactCard Card(string root) => new()
    {
        ModelArtifactId = "test-h3", DisplayName = "Test H3", IsManaged = true, Revision = "r1", Role = ManagedModelRoles.Tool,
        ModelsRoot = root, InstallDirectory = Path.Combine(root, "owned"),
        Files = [new() { RelativePath = "model.bin", SizeBytes = Bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Bytes)), SourceUrl = "https://test.invalid/model.bin" }]
    };
    private sealed class Progress(Action<ManagedModelDownloadProgress> update) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress value) => update(value); }
    private sealed class Network : HttpMessageHandler
    {
        internal int Calls, Active, Peak;
        internal bool Fail;
        internal CancellationTokenSource? CancelOnSecondRequest;
        internal List<long> Starts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls); var active = Interlocked.Increment(ref Active);
            lock (Starts) Peak = Math.Max(Peak, active);
            try
            {
                if (Fail) throw new HttpRequestException("No network expected.");
                if (call == 2) CancelOnSecondRequest?.Cancel();
                await Task.Delay(30, token);
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range?.From is not { } from) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
                lock (Starts) Starts.Add(from);
                var end = range.To ?? Bytes.Length - 1;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Bytes[(int)from..((int)end + 1)]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, end, Bytes.Length); return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    [TestMethod]
    public void BundledManifestPinsModelsWorkflowCodeAndLicenses()
    {
        using var f = new VideoPreparationTests.TestFiles();
        var cards = VideoH3Catalog.Cards(f.File("models")); Assert.IsGreaterThanOrEqualTo(10, cards.Count);
        Assert.IsTrue(cards.All(c => c.Revision.Length == 40 && c.IsManaged && c.IsPinned));
        Assert.IsTrue(cards.SelectMany(c => c.Files).Any(f => f.RelativePath.EndsWith("qwen3vl_32b_minimax_h3_int8_convrot.safetensors", StringComparison.Ordinal)));
        Assert.IsFalse(cards.SelectMany(c => c.Files).Any(f => f.RelativePath.Contains("nvfp4", StringComparison.OrdinalIgnoreCase)));
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), f.File("receipts.json"));
        foreach (var card in cards)
        {
            var terms = licenses.Entries.Single(l => l.Id == card.ModelArtifactId);
            Assert.IsTrue(terms.Texts.Length > 0);
            foreach (var text in terms.Texts) Assert.IsGreaterThan(40, licenses.ReadText(text).Length);
            foreach (var file in card.Files) Assert.IsTrue(file.SourceUrl.Contains(card.Revision, StringComparison.Ordinal));
        }
        Assert.Throws<InvalidOperationException>(() => VideoH3Catalog.Cards(""));
        Assert.Throws<InvalidOperationException>(() => VideoH3Catalog.Cards("relative"));
        foreach (var card in cards.Where(c => c.ModelArtifactId is not ("video-h3-models" or "video-h3-upscale-model")))
            Assert.IsTrue(card.Files.All(file => file.SourceUrl.StartsWith("https://github.com/PiTrolKun/LOPATA/releases/download/h3-components-20261029/", StringComparison.Ordinal)));
        Assert.IsTrue(cards.Single(c => c.ModelArtifactId == "video-h3-preview-model").SourcePage.StartsWith("https://huggingface.co/Kijai/", StringComparison.Ordinal));
    }
    [TestMethod]
    public async Task VerifyMissingFilesUsesNoNetworkOrConsentAndCancelledOperationDoesNotRegister()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var store = new ManagedModelLibraryStore(f.File("library"));
        var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoH3Preparation(card.ModelsRoot, store, http, [card]);
        Assert.IsFalse(await service.PrepareAsync(false, null, default)); Assert.AreEqual(0, network.Calls);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, cancel.Token));
        Assert.IsFalse(Directory.Exists(card.InstallDirectory));
    }
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task FirstAuditFullThenModeRevisionAndReplacementAreHonored(bool enabled)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), enabled);
        var card = Card(f.File("models")); Directory.CreateDirectory(card.InstallDirectory); var path = Path.Combine(card.InstallDirectory, "model.bin");
        await File.WriteAllBytesAsync(path, Bytes);
        var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoH3Preparation(card.ModelsRoot, new(f.File("library")), http, [card]);
        var stages = new List<string>(); var progress = new Progress(p => stages.Add(p.Stage));
        Assert.IsTrue(await service.PrepareAsync(false, progress, default)); Assert.Contains("verified", stages);
        stages.Clear(); Assert.IsTrue(await service.PrepareAsync(false, progress, default));
        Assert.Contains(enabled ? "verified" : "verifying-presence", stages);
        stages.Clear(); card.Revision = "r2"; Assert.IsTrue(await service.PrepareAsync(false, progress, default)); Assert.Contains("verified", stages);
        ComponentAudit.Invalidate(card.InstallDirectory); await File.WriteAllBytesAsync(path, new byte[Bytes.Length]);
        Assert.IsFalse(await service.PrepareAsync(false, null, default));
        File.Delete(path); Assert.IsFalse(await service.PrepareAsync(false, null, default)); Assert.AreEqual(0, network.Calls);
    }
    [TestMethod]
    public async Task CommonDownloaderResumesPartialWithMultipleConnectionsAndFirstUseAuditRemains()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); Directory.CreateDirectory(card.InstallDirectory);
        var target = Path.Combine(card.InstallDirectory, "model.bin"); await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
        var network = new Network(); using var http = new HttpClient(network);
        using var service = new VideoH3Preparation(card.ModelsRoot, new(f.File("library")), http, [card], 16384) { ParallelConnections = 4 };
        Assert.IsTrue(await service.PrepareAsync(true, null, default));
        Assert.IsGreaterThan(1, network.Peak); Assert.IsTrue(network.Starts.All(n => n >= 65536));
        CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(target));
        var stages = new List<string>(); Assert.IsTrue(await service.PrepareAsync(false, new Progress(p => stages.Add(p.Stage)), default)); Assert.Contains("verified", stages);
        stages.Clear(); Assert.IsTrue(await service.PrepareAsync(false, new Progress(p => stages.Add(p.Stage)), default)); Assert.Contains("verifying-presence", stages);
    }
    [TestMethod]
    public async Task DeclinedTermsWriteNothingAndCancellationPreservesPartialAndUnrelatedFiles()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var network = new Network(); using var http = new HttpClient(network);
        using var service = new VideoH3Preparation(card.ModelsRoot, new(f.File("library")), http, [card], 16384) { ParallelConnections = 4 };
        var gate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, ct) => throw new OperationCanceledException("Declined", ct);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, default));
            Assert.IsFalse(Directory.Exists(card.InstallDirectory)); Assert.AreEqual(0, network.Calls);
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
        Directory.CreateDirectory(card.InstallDirectory); var target = Path.Combine(card.InstallDirectory, "model.bin");
        await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
        await File.WriteAllTextAsync(Path.Combine(card.InstallDirectory, "user.txt"), "keep");
        using var cancel = new CancellationTokenSource(); network.CancelOnSecondRequest = cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, cancel.Token));
        Assert.IsFalse(File.Exists(target)); Assert.IsTrue(File.Exists(target + ".part"));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Combine(card.InstallDirectory, "user.txt")));
        network.CancelOnSecondRequest = null;
        Assert.IsTrue(await service.PrepareAsync(true, null, default)); CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(target));
    }
}
