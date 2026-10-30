using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoVacePreparationTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i * 23)).ToArray();
    private static ManagedModelArtifactCard Card(string root) => new()
    {
        ModelArtifactId = "test-vace", DisplayName = "Test VACE", IsManaged = true, IsPinned = true, Revision = "r1",
        Role = ManagedModelRoles.Tool, ModelsRoot = root, InstallDirectory = Path.Combine(root, "owned"),
        Files = Enumerable.Range(0, 3).Select(i => new ManagedModelArtifactFile
        {
            RelativePath = "weights/part" + i + ".bin", SizeBytes = Bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Bytes)), SourceUrl = "https://models.invalid/part" + i + ".bin"
        }).ToList()
    };
    private sealed class Progress(Action<ManagedModelDownloadProgress> update) : IProgress<ManagedModelDownloadProgress>
    { public void Report(ManagedModelDownloadProgress value) => update(value); }
    private sealed class Network : HttpMessageHandler
    {
        internal int Calls, Active, Peak;
        internal bool Fail;
        internal CancellationTokenSource? CancelOnSecondRequest;
        internal List<(string Path, long Start)> Starts = [];
        internal bool HadAuthorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref Calls); var active = Interlocked.Increment(ref Active);
            lock (Starts) { Peak = Math.Max(Peak, active); HadAuthorization |= request.Headers.Authorization is not null; }
            try
            {
                if (Fail) throw new HttpRequestException("No network expected.");
                if (call == 2) CancelOnSecondRequest?.Cancel();
                await Task.Delay(25, token);
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range?.From is not { } from) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
                lock (Starts) Starts.Add((request.RequestUri!.AbsolutePath, from));
                var end = range.To ?? Bytes.Length - 1;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(Bytes[(int)from..((int)end + 1)]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, end, Bytes.Length);
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
    private static async Task WriteAllAsync(ManagedModelArtifactCard card)
    {
        foreach (var file in card.Files)
        {
            var path = Path.Combine(card.InstallDirectory, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Bytes);
        }
    }

    [TestMethod]
    public void CatalogPinsExactPublicThreeFileBundleAndCreatesIndependentCards()
    {
        using var f = new VideoPreparationTests.TestFiles();
        var root = f.File("models"); var card = VideoVaceCatalog.Cards(root).Single();
        Assert.AreEqual("video-vace13-models", card.ModelArtifactId);
        CollectionAssert.AreEqual(new[] { card.ModelArtifactId }, VideoVaceCatalog.ComponentIds.ToArray());
        Assert.AreEqual("Comfy-Org/Wan_2.1_ComfyUI_repackaged", card.RepositoryId);
        Assert.AreEqual("123acf1cc74bccbb9bfff8ac1ee72edc08c2341d", card.Revision);
        Assert.AreEqual(11_299_242_015L, card.TotalBytes);
        Assert.IsTrue(card.IsManaged && card.IsPinned && !card.IsSystem && !card.CanRemoveFiles);
        Assert.AreEqual(ManagedModelRoles.Tool, card.Role); Assert.AreEqual("Apache-2.0", card.License);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "Video", "VACE-1.3B", card.ModelArtifactId, card.Revision), card.InstallDirectory);
        var expected = new[]
        {
            ("split_files/diffusion_models/wan2.1_vace_1.3B_fp16.safetensors", 4_309_519_800L,
                "640ccc0577e6a5d4bb15cd91b11b699ef914fc55f126c5a1c544e152130784f2"),
            ("split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", 6_735_906_897L,
                "c3355d30191f1f066b26d93fba017ae9809dce6c627dda5f6a66eaa651204f68"),
            ("split_files/vae/wan_2.1_vae.safetensors", 253_815_318L,
                "2fc39d31359a4b0a64f55876d8ff7fa8d780956ae2cb13463b0223e15148976b")
        };
        Assert.AreEqual(expected.Length, card.Files.Count);
        foreach (var (path, size, hash) in expected)
        {
            var file = card.Files.Single(file => file.RelativePath == path);
            Assert.AreEqual(size, file.SizeBytes); Assert.AreEqual(hash, file.Sha256); Assert.IsTrue(file.IsRequired);
            Assert.AreEqual("https://huggingface.co/" + card.RepositoryId + "/resolve/" + card.Revision + "/" + path, file.SourceUrl);
        }
        card.Files[0].Sha256 = "untrusted";
        Assert.AreEqual(expected[0].Item3, VideoVaceCatalog.Cards(root).Single().Files[0].Sha256);
        Assert.IsFalse(Directory.Exists(root));
        Assert.Throws<InvalidOperationException>(() => VideoVaceCatalog.Cards(""));
        Assert.Throws<InvalidOperationException>(() => VideoVaceCatalog.Cards("relative"));
    }

    [TestMethod]
    public async Task MissingAndPartialFilesAreLocalStatesWithoutNetworkOrConsent()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var store = new ManagedModelLibraryStore(f.File("library"));
        var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoVacePreparation(card.ModelsRoot, store, http, [card]);
        var gate = ComponentLicenseGate.ConfirmAsync; var consent = 0;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, _) => { consent++; return Task.CompletedTask; };
            Assert.IsFalse(await service.PrepareAsync(false, null, default));
            Assert.IsTrue(service.Inspect().All(file => file.Stored == 0 && !file.Downloaded));
            Assert.IsFalse(Directory.Exists(card.InstallDirectory));
            var target = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
            var state = new VideoH3DownloadState(service.Cards); state.Refresh(service.Inspect());
            Assert.AreEqual("partial", state.Rows[0].Stage); Assert.AreEqual(65536L, state.StoredBytes);
            Assert.IsTrue(state.Rows.Skip(1).All(row => row.Stage == "waiting"));
            Assert.IsFalse(await service.PrepareAsync(false, null, default));
            Assert.AreEqual(0, network.Calls); Assert.AreEqual(0, consent);
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task FirstFullAuditThenSharedPolicyRevisionAndReplacementAreHonored(bool enabled)
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), enabled);
        var card = Card(f.File("models")); await WriteAllAsync(card);
        var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoVacePreparation(card.ModelsRoot, new(f.File("library")), http, [card]);
        var stages = new List<string>(); var progress = new Progress(p => stages.Add(p.Stage));
        Assert.IsTrue(await service.PrepareAsync(false, progress, default)); Assert.Contains("verifying", stages);
        stages.Clear(); Assert.IsTrue(await service.PrepareAsync(false, progress, default));
        Assert.Contains(enabled ? "verifying" : "verifying-presence", stages);
        Assert.AreEqual(enabled, stages.Contains("verifying"));
        var path = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
        await File.WriteAllBytesAsync(path, [0]);
        Assert.AreEqual(!enabled, await service.PrepareAsync(false, null, default));
        ComponentAudit.Invalidate(card.InstallDirectory);
        Assert.IsFalse(await service.PrepareAsync(false, null, default));
        await File.WriteAllBytesAsync(path, Bytes);
        Assert.IsTrue(await service.PrepareAsync(false, null, default));
        stages.Clear(); card.Revision = "r2";
        Assert.IsTrue(await service.PrepareAsync(false, progress, default)); Assert.Contains("verifying", stages);
        File.Delete(path); Assert.IsFalse(await service.PrepareAsync(false, null, default)); Assert.AreEqual(0, network.Calls);
    }

    [TestMethod]
    public async Task CommonDownloaderResumesInParallelAndDoesNotConsumeFirstUseAudit()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var target = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
        var network = new Network(); using var http = new HttpClient(network);
        using var service = new VideoVacePreparation(card.ModelsRoot, new(f.File("library")), http, [card], 16384) { ParallelConnections = 4 };
        Assert.AreEqual(4, service.ParallelConnections);
        Assert.IsTrue(await service.PrepareAsync(true, null, default)); Assert.IsGreaterThan(1, network.Peak);
        Assert.IsTrue(network.Starts.Where(item => item.Path.EndsWith("part0.bin", StringComparison.Ordinal)).All(item => item.Start >= 65536));
        foreach (var file in card.Files) CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(Path.Combine(card.InstallDirectory, file.RelativePath)));
        Assert.IsFalse(network.HadAuthorization);
        var stages = new List<string>(); var progress = new Progress(p => stages.Add(p.Stage));
        Assert.IsTrue(await service.PrepareAsync(false, progress, default)); Assert.Contains("verifying", stages);
        stages.Clear(); Assert.IsTrue(await service.PrepareAsync(false, progress, default));
        Assert.Contains("verifying-presence", stages); Assert.DoesNotContain("verifying", stages);
    }

    [TestMethod]
    public async Task AuthoritativeCatalogOverridesMutableLibraryPinsAndExternalDirectory()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var expected = Card(f.File("models")); await WriteAllAsync(expected);
        var previous = Card(expected.ModelsRoot); previous.InstallDirectory = f.File("external");
        previous.Status = ManagedModelStatuses.Installed;
        foreach (var file in previous.Files) { file.Sha256 = new string('0', 64); file.SourceUrl = "https://untrusted.invalid/model"; }
        var store = new ManagedModelLibraryStore(f.File("library")); store.Upsert(previous);
        var network = new Network { Fail = true }; using var http = new HttpClient(network);
        using var service = new VideoVacePreparation(expected.ModelsRoot, store, http, [expected]);
        Assert.IsTrue(await service.PrepareAsync(false, null, default)); Assert.AreEqual(0, network.Calls);
        var saved = store.Load(expected.ModelArtifactId)!;
        Assert.AreEqual(expected.InstallDirectory, saved.InstallDirectory);
        Assert.IsTrue(saved.Files.All(file => file.Sha256 == expected.Files[0].Sha256 && new Uri(file.SourceUrl).Host == "models.invalid"));
        Assert.IsFalse(Directory.Exists(previous.InstallDirectory));
    }

    [TestMethod]
    public async Task CancellationAndDeclinedConsentPreservePartialAndUnrelatedFiles()
    {
        using var f = new VideoPreparationTests.TestFiles(); using var audit = ComponentAudit.UseTestStore(f.File("audit"), false);
        var card = Card(f.File("models")); var store = new ManagedModelLibraryStore(f.File("library"));
        var network = new Network(); using var http = new HttpClient(network);
        using var service = new VideoVacePreparation(card.ModelsRoot, store, http, [card], 16384) { ParallelConnections = 4 };
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, cancelled.Token));
        Assert.IsNull(store.Load(card.ModelArtifactId)); Assert.IsFalse(Directory.Exists(card.InstallDirectory));
        var gate = ComponentLicenseGate.ConfirmAsync;
        try
        {
            ComponentLicenseGate.ConfirmAsync = (_, token) => throw new OperationCanceledException("Declined", token);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, default));
            Assert.AreEqual(0, network.Calls); Assert.IsFalse(Directory.Exists(card.InstallDirectory));
        }
        finally { ComponentLicenseGate.ConfirmAsync = gate; }
        var target = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target + ".part", Bytes[..65536]);
        var unrelated = Path.Combine(card.InstallDirectory, "user.txt"); await File.WriteAllTextAsync(unrelated, "keep");
        using var cancel = new CancellationTokenSource(); network.CancelOnSecondRequest = cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(true, null, cancel.Token));
        Assert.IsFalse(File.Exists(target)); Assert.IsTrue(File.Exists(target + ".part"));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(unrelated));
        network.CancelOnSecondRequest = null;
        Assert.IsTrue(await service.PrepareAsync(true, null, default));
        CollectionAssert.AreEqual(Bytes, await File.ReadAllBytesAsync(target));
    }
}
