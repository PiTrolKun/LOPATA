using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using Lopata.Updates;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class EngineSetTests
{
    [TestMethod]
    public async Task FreshInstallationOnlyNeedsTheRecommendedSet()
    {
        using var f = new Fixture(); f.Install("002", bundled: true);
        var service = f.Service(); await service.InitializeAsync();
        Assert.AreEqual("002", service.GetState().ActiveId);
        CollectionAssert.AreEqual(new[] { "002" }, (await service.GetInstalledAsync()).ToArray());
        Assert.IsNull(await service.GetOfferAsync()); Assert.AreEqual(0, f.Network.Requests);
    }

    [TestMethod]
    public async Task ManualRollbackSurvivesUpdateAndDeclineUntilAnotherRecommendation()
    {
        using var f = new Fixture("004", "005", "006", "007");
        foreach (var id in new[] { "004", "005", "006", "007" }) f.Install(id);
        var old = f.Service("005"); await old.InitializeAsync(); await old.ActivateAsync("004");
        var update = f.Service("006"); await update.InitializeAsync();
        Assert.AreEqual("004", update.GetState().ActiveId);
        Assert.AreEqual("006", (await update.GetOfferAsync())!.Id);
        update.Decline("006");
        var restarted = f.Service("006"); await restarted.InitializeAsync();
        Assert.AreEqual("004", restarted.GetState().ActiveId); Assert.IsNull(await restarted.GetOfferAsync());
        var next = f.Service("007"); await next.InitializeAsync();
        Assert.AreEqual("007", (await next.GetOfferAsync())!.Id);
        await next.ActivateAsync("007"); Assert.AreEqual("007", next.GetState().ActiveId);
        Assert.IsTrue(next.GetState().ManualSelection); Assert.IsTrue(File.Exists(f.RuntimeFile("004")));
    }

    [TestMethod]
    public async Task CancelledOfferDoesNotChangeTheActiveSelection()
    {
        using var f = new Fixture(); f.Install("001"); f.Install("002");
        var service = f.Service(); await service.InitializeAsync();
        Assert.AreEqual("001", service.GetState().ActiveId);
        Assert.AreEqual("002", (await service.GetOfferAsync())!.Id);
        var restarted = f.Service(); await restarted.InitializeAsync();
        Assert.AreEqual("001", restarted.GetState().ActiveId);
    }

    [TestMethod]
    public async Task CorruptionAndBusyOperationNeverChangeTheChoice()
    {
        using var f = new Fixture(); f.Install("001"); f.Install("002");
        var service = f.Service(); await service.InitializeAsync();
        File.WriteAllText(f.RuntimeFile("002"), "damaged");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ActivateAsync("002"));
        Assert.AreEqual("001", service.GetState().ActiveId);
        f.Install("002"); var busy = f.Service(busy: () => true); await busy.InitializeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => busy.ActivateAsync("002"));
        Assert.AreEqual("001", busy.GetState().ActiveId);
    }

    [TestMethod]
    public async Task RepeatedInventoryAndActivationStillRejectSameSizeDamageWithRestoredTimestamp()
    {
        using var f = new Fixture(); f.Install("001"); f.Install("002");
        var service = f.Service(); await service.InitializeAsync();
        Assert.IsTrue((await service.GetInstalledAsync()).Contains("002"));
        var file = f.RuntimeFile("002");
        var modified = File.GetLastWriteTimeUtc(file); var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 1; File.WriteAllBytes(file, bytes); File.SetLastWriteTimeUtc(file, modified);
        Assert.IsFalse((await service.GetInstalledAsync()).Contains("002"), "Inventory must still hash content, not trust size or timestamps.");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ActivateAsync("002"));
        Assert.AreEqual("001", service.GetState().ActiveId);
    }

    [TestMethod]
    public async Task UnsupportedModelRequirementDoesNotAllowRollback()
    {
        using var f = new Fixture(); f.Install("001"); f.Install("002");
        var service = f.Service(required: () => ["model.future-only"]); await service.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ActivateAsync("001"));
        Assert.IsFalse(service.GetState().ManualSelection);
    }

    [TestMethod]
    public async Task LegacyMigrationCopiesOnlyMatchingFilesAndPreservesOriginals()
    {
        using var f = new Fixture(); f.Install("002", bundled: true);
        var legacy = Path.Combine(f.Root, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "llama-server.exe"), Fixture.Text("001"));
        var service = f.Service(legacy: new Dictionary<string, string> { ["llama/cpu"] = legacy });
        await service.InitializeAsync();
        Assert.AreEqual("001", service.GetState().ActiveId);
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(f.RuntimeFile("001")));
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(Path.Combine(legacy, "llama-server.exe")));
        Assert.AreEqual("002", (await service.GetOfferAsync())!.Id);
    }

    [TestMethod]
    public async Task UnverifiedLegacyFileIsNeverRegisteredAsAnInstalledSet()
    {
        using var f = new Fixture(); f.Install("002", bundled: true);
        var legacy = Path.Combine(f.Root, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "llama-server.exe"), "unverified");
        var service = f.Service(legacy: new Dictionary<string, string> { ["llama/cpu"] = legacy });
        await service.InitializeAsync(); Assert.AreEqual("002", service.GetState().ActiveId);
        Assert.IsFalse(File.Exists(f.RuntimeFile("001")));
    }

    [TestMethod]
    public async Task PartialOptionalCopyDoesNotShadowACompleteBundledSet()
    {
        using var f = new Fixture(); f.Install("001", bundled: true); f.Install("002", bundled: true);
        var partial = f.RuntimeFile("001"); Directory.CreateDirectory(Path.GetDirectoryName(partial)!); File.WriteAllText(partial, "partial");
        var service = f.Service(); await service.InitializeAsync();
        Assert.IsTrue((await service.GetInstalledAsync()).Contains("001"));
        Assert.AreEqual(Path.GetDirectoryName(f.RuntimeFile("001", bundled: true)), EngineSetSelection.ResolveDirectory("001", "llama", "cpu"));
    }

    [TestMethod]
    public void EvenASignedCatalogCannotDeclareAnIncompleteRuntimeReady()
    {
        using var f = new Fixture();
        var original = JsonSerializer.Deserialize<UpdateManifest>(Convert.FromBase64String(f.Sets[0].Manifest.Payload), UpdateManifest.JsonOptions)!;
        var incomplete = original with { Files = original.Files.Where(file => file.Path != "sd/cpu/sd-cli.exe").ToArray() };
        var set = f.Sets[0] with { Manifest = f.Sign(incomplete) };
        var error = Assert.Throws<InvalidDataException>(() => f.Service(recommended: "001", sets: [set]));
        StringAssert.Contains(error.Message, "required runtime entry point");
    }

    [TestMethod]
    public async Task DownloadIsVerifiedAndDoesNotActivateTheDownloadedSet()
    {
        using var f = new Fixture(); f.Install("002", bundled: true);
        var service = f.Service(); await service.InitializeAsync();
        await service.DownloadAsync("001");
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(f.RuntimeFile("001")));
        Assert.AreEqual("002", service.GetState().ActiveId);
        await service.ActivateAsync("001"); Assert.AreEqual("001", service.GetState().ActiveId);
    }

    [TestMethod]
    public async Task MatchingMigratedPackageIsReusedWithoutASecondDownload()
    {
        using var f = new Fixture(); f.Install("002", bundled: true); f.SplitPackages("001");
        var partial = f.RuntimeFile("001"); Directory.CreateDirectory(Path.GetDirectoryName(partial)!); File.WriteAllText(partial, Fixture.Text("001"));
        var service = f.Service(); await service.InitializeAsync(); await service.DownloadAsync("001");
        Assert.IsFalse(f.Network.RequestedNames.Contains("keep-001.zip"));
        Assert.IsTrue(f.Network.RequestedNames.Contains("rest-001.zip"));
        Assert.IsTrue((await service.GetInstalledAsync()).Contains("001"));
        Assert.AreEqual("002", service.GetState().ActiveId);
        Assert.IsEmpty(f.PreparationDirectories());
    }

    [TestMethod]
    public async Task TruncatedDownloadRemainsUninstalledAndCanBeRetried()
    {
        using var f = new Fixture(); f.Install("002", bundled: true); f.SplitPackages("001"); f.Network.Truncated = true;
        var partial = f.RuntimeFile("001"); Directory.CreateDirectory(Path.GetDirectoryName(partial)!); File.WriteAllText(partial, Fixture.Text("001"));
        var oldBytes = File.ReadAllBytes(f.RuntimeFile("002", bundled: true));
        var service = f.Service(); await service.InitializeAsync();
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync("001"));
        Assert.IsEmpty(f.PreparationDirectories()); Assert.AreEqual("002", service.GetState().ActiveId);
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(partial));
        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(f.RuntimeFile("002", bundled: true)));
        Assert.IsTrue(new FileInfo(f.CachePart("rest-001.zip")).Length > 0, "Verified downloader partial data must remain resumable.");
        f.Network.Truncated = false; await service.DownloadAsync("001");
        Assert.IsTrue((await service.GetInstalledAsync()).Contains("001"));
        Assert.IsEmpty(f.PreparationDirectories());
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(f.Root, "installed"), ".damaged-*"), "Prior recovery directory must not be cleaned with staging.");
    }

    [TestMethod]
    public async Task CancelledPreparationRemovesOnlyItsOwnStageAndKeepsResumableCache()
    {
        using var f = new Fixture(); f.Install("002", bundled: true); f.SplitPackages("001");
        var partial = f.RuntimeFile("001"); Directory.CreateDirectory(Path.GetDirectoryName(partial)!); File.WriteAllText(partial, Fixture.Text("001"));
        var oldBytes = File.ReadAllBytes(f.RuntimeFile("002", bundled: true));
        using var cancellation = new CancellationTokenSource(); f.Network.CancelAfterFirstRead = cancellation.Cancel;
        var service = f.Service(); await service.InitializeAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync("001", ct: cancellation.Token));
        Assert.IsEmpty(f.PreparationDirectories()); Assert.AreEqual("002", service.GetState().ActiveId);
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(partial));
        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(f.RuntimeFile("002", bundled: true)));
        Assert.IsTrue(new FileInfo(f.CachePart("rest-001.zip")).Length > 0);
        f.Network.CancelAfterFirstRead = null; await service.DownloadAsync("001");
        Assert.IsTrue((await service.GetInstalledAsync()).Contains("001")); Assert.IsEmpty(f.PreparationDirectories());
        CollectionAssert.AreEqual(oldBytes, File.ReadAllBytes(f.RuntimeFile("002", bundled: true)));
    }

    [TestMethod]
    public async Task CancellationBeforeActivationCommitPreservesTheDurableSelection()
    {
        using var f = new Fixture(); f.Install("001"); f.Install("002");
        using var cancellation = new CancellationTokenSource(); var checks = 0;
        var service = f.Service(busy: () => { if (++checks == 2) cancellation.Cancel(); return false; });
        await service.InitializeAsync(); var before = File.ReadAllBytes(f.State);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ActivateAsync("002", cancellation.Token));
        Assert.AreEqual("001", service.GetState().ActiveId); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.State));
    }

    [TestMethod]
    public void PreparationCleanupRejectsOutsidePathsAndKeepsRecoveryDirectories()
    {
        using var f = new Fixture(); f.Install("001");
        var installed = Path.Combine(f.Root, "installed");
        var leaf = ".prepare-001-" + Guid.NewGuid().ToString("N");
        var outside = Path.Combine(f.Root, leaf); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        Assert.Throws<InvalidDataException>(() => EngineSetStageCleanup.DeleteOwned(installed, outside, leaf));
        Assert.AreEqual("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        var owned = Path.Combine(installed, leaf); Directory.CreateDirectory(Path.Combine(owned, "app", "studio"));
        File.WriteAllText(Path.Combine(owned, "app", "studio", "copied.dll"), "temporary payload");
        var recovery = Path.Combine(installed, ".damaged-001-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(recovery);
        File.WriteAllText(Path.Combine(recovery, "keep.dll"), "recovery");
        EngineSetStageCleanup.DeleteOwned(installed, owned, leaf);
        Assert.IsFalse(Directory.Exists(owned)); Assert.AreEqual("recovery", File.ReadAllText(Path.Combine(recovery, "keep.dll")));
        Assert.AreEqual(Fixture.Text("001"), File.ReadAllText(f.RuntimeFile("001")));
    }

    [TestMethod]
    public async Task UnpublishedArtifactsReturnAnHonestErrorWithoutChangingTheSelection()
    {
        using var f = new Fixture(); f.Install("002", bundled: true); f.Network.Missing = true;
        var service = f.Service(); await service.InitializeAsync();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync("001"));
        Assert.AreEqual(HttpStatusCode.NotFound, error.StatusCode);
        Assert.AreEqual("002", service.GetState().ActiveId); Assert.IsFalse(File.Exists(f.RuntimeFile("001")));
    }

    [TestMethod]
    public void SignatureAndUnsafePathAreRejectedBeforeAcquisition()
    {
        using var f = new Fixture();
        var definition = f.Sets[0] with { Manifest = f.Sets[0].Manifest with { Signature = Convert.ToBase64String(new byte[64]) } };
        Assert.Throws<InvalidDataException>(() => f.Service(sets: [definition]));
        Assert.Throws<InvalidDataException>(() => EngineSetSelection.ResolveDirectory("../002", "sd", "cpu"));
        Assert.Throws<InvalidDataException>(() => EngineSetSelection.ResolveDirectory("002", "../sd", "cpu"));
    }

    [TestMethod]
    public async Task CorruptChoiceStateIsNotSilentlyReset()
    {
        using var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.State)!);
        File.WriteAllText(f.State, "{broken"); var before = File.ReadAllBytes(f.State);
        await Assert.ThrowsAsync<JsonException>(() => f.Service().InitializeAsync());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(f.State));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lopata-engine-test-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(Root, "state", "choice.json");
        public EngineSetDefinition[] Sets { get; }
        public FakeNetwork Network { get; } = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _originalActive = EngineSetSelection.ActiveId;
        private readonly string _originalRecommended = EngineSetSelection.RecommendedId;
        public Fixture(params string[] ids)
        {
            if (ids.Length == 0) ids = ["001", "002"];
            Directory.CreateDirectory(Root);
            Sets = ids.Select(id =>
            {
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var (name, text) in Contents(id))
                    { using var output = zip.CreateEntry("app/" + name).Open(); output.Write(Encoding.UTF8.GetBytes(text)); }
                var bytes = memory.ToArray(); var package = "engine-" + id + ".zip";
                Network.Archives[package] = bytes;
                var manifest = new UpdateManifest { Version = "0.5.78-beta", SourceCommit = new('a', 40),
                    Files = Contents(id).Select(p => new UpdateFile("app", p.Key, Encoding.UTF8.GetByteCount(p.Value), Hash(Encoding.UTF8.GetBytes(p.Value)), package)).ToArray(),
                    Packages = [new(package, "https://github.com/PiTrolKun/LOPATA/releases/download/v0.5.78-beta/" + package, bytes.Length, Hash(bytes))],
                    Notes = [new("0.5.78-beta", DateTimeOffset.UtcNow, "Test set")] };
                return new EngineSetDefinition { Id = id, Date = "2026-10-18", DescriptionKey = "description", LimitKey = "limit",
                    SupportedCapabilities = ["chat.gguf", "image.z-image", "image.flux", "music.studio"],
                    LicenseIds = ["test-license"],
                    Manifest = SignedManifest.Sign(manifest, "test", _key) };
            }).ToArray();
        }
        public EngineSetService Service(string? recommended = null, Func<bool>? busy = null,
            Func<IReadOnlyCollection<string>>? required = null, IReadOnlyDictionary<string, string>? legacy = null,
            EngineSetDefinition[]? sets = null) => new(new EngineSetCatalog { Sets = sets ?? Sets,
                RecommendedId = recommended ?? "002" }, new Dictionary<string, string> { ["test"] = _key.ExportSubjectPublicKeyInfoPem() },
                State, Path.Combine(Root, "installed"), Path.Combine(Root, "bundled"), new HttpClient(Network, false), busy, required, legacy);
        public SignedManifest Sign(UpdateManifest manifest) => SignedManifest.Sign(manifest, "test", _key);
        public void SplitPackages(string id)
        {
            var index = Array.FindIndex(Sets, set => set.Id == id);
            var original = JsonSerializer.Deserialize<UpdateManifest>(Convert.FromBase64String(Sets[index].Manifest.Payload), UpdateManifest.JsonOptions)!;
            var files = new List<UpdateFile>(); var packages = new List<UpdatePackage>();
            foreach (var group in Contents(id).GroupBy(pair => pair.Key == "llama/cpu/llama-server.exe" ? "keep" : "rest"))
            {
                var name = group.Key + "-" + id + ".zip";
                using var memory = new MemoryStream();
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                    foreach (var (path, text) in group)
                    { using var output = zip.CreateEntry("app/" + path).Open(); output.Write(Encoding.UTF8.GetBytes(text)); }
                var bytes = memory.ToArray(); Network.Archives[name] = bytes;
                packages.Add(new(name, "https://github.com/PiTrolKun/LOPATA/releases/download/v0.5.78-beta/" + name, bytes.Length, Hash(bytes)));
                files.AddRange(group.Select(pair => original.Files.Single(file => file.Path == pair.Key) with { Package = name }));
            }
            Sets[index] = Sets[index] with { Manifest = Sign(original with { Files = files.ToArray(), Packages = packages.ToArray() }) };
        }
        public string RuntimeFile(string id, bool bundled = false) => Path.Combine(Root, bundled ? "bundled" : "installed", id, "llama", "cpu", "llama-server.exe");
        public string CachePart(string package) => Path.Combine(Root, "state", "EngineSetDownloads", "001", package + ".part");
        public string[] PreparationDirectories() => Directory.Exists(Path.Combine(Root, "installed"))
            ? Directory.GetDirectories(Path.Combine(Root, "installed"), ".prepare-*") : [];
        public void Install(string id, bool bundled = false)
        {
            foreach (var (name, text) in Contents(id))
            { var path = Path.Combine(Root, bundled ? "bundled" : "installed", id, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        }
        private static Dictionary<string, string> Contents(string id) => new()
        {
            ["llama/cpu/llama-server.exe"] = Text(id), ["llama/vulkan/llama-server.exe"] = Text(id),
            ["sd/cpu/sd-cli.exe"] = Text(id), ["sd/vulkan/sd-cli.exe"] = Text(id), ["sd/cuda12/sd-cli.exe"] = Text(id),
            ["studio/native/music-server.exe"] = Text(id), ["studio/native/engine/yue-server.exe"] = Text(id),
            ["studio/native/manifest.json"] = "{}"
        };
        public static string Text(string id) => "native engine " + id;
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose()
        {
            typeof(EngineSetSelection).GetMethod("Publish", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [_originalActive, _originalRecommended]);
            _key.Dispose(); Network.Dispose();
            var absolute = Path.GetFullPath(Root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("lopata-engine-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup.");
            Directory.Delete(absolute, true);
        }
    }
    private sealed class FakeNetwork : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Archives { get; } = new();
        public bool Missing { get; set; }
        public bool Truncated { get; set; }
        public Action? CancelAfterFirstRead { get; set; }
        public int Requests { get; private set; }
        public List<string> RequestedNames { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; token.ThrowIfCancellationRequested();
            RequestedNames.Add(Path.GetFileName(request.RequestUri!.AbsolutePath));
            if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var bytes = Archives[Path.GetFileName(request.RequestUri!.AbsolutePath)];
            HttpContent content = CancelAfterFirstRead is { } cancel
                ? new StreamContent(new PartialCancellationStream(bytes, cancel))
                : new ByteArrayContent(Truncated ? bytes[..(bytes.Length / 2)] : bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class PartialCancellationStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        private int _reads;
        private readonly int _firstChunkLength = Math.Max(1, bytes.Length / 2);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0) { cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, _firstChunkLength)], cancellationToken);
        }
    }
}
