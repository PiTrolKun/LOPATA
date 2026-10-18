using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class EngineSetDeliveryTests
{
    [TestMethod]
    public async Task DeltaKeepsRetiredSetsAndLegacyStudioWithoutMakingThemMandatory()
    {
        using var f = new UpdateFixture();
        f.Put("EngineSets/004/llama/cpu/server.exe", "four");
        f.Put("EngineSets/005/llama/cpu/server.exe", "five");
        f.Put("MusicStudioRuntime/music-server.exe", "legacy"); f.Put("obsolete.dll", "obsolete");
        var previous = UpdateFixture.Manifest("0.5.77-beta", UpdateFixture.Entry("EngineSets/004/llama/cpu/server.exe", "four"),
            UpdateFixture.Entry("EngineSets/005/llama/cpu/server.exe", "five"), UpdateFixture.Entry("MusicStudioRuntime/music-server.exe", "legacy"),
            UpdateFixture.Entry("obsolete.dll", "obsolete"));
        var target = UpdateFixture.Manifest("0.5.78-beta", UpdateFixture.Entry("EngineSets/006/llama/cpu/server.exe", "six"));
        var plan = await UpdatePlanner.CreateAsync(previous, target, f.Roots);
        CollectionAssert.AreEqual(new[] { "obsolete.dll" }, plan.Operations.Where(o => o.Action == UpdateAction.Delete).Select(o => o.File.Path).ToArray());
        var fresh = await UpdatePlanner.CreateAsync(null, target, f.Roots);
        CollectionAssert.AreEqual(new[] { "EngineSets/006/llama/cpu/server.exe" }, fresh.Operations.Select(o => o.File.Path).ToArray());
        Assert.AreEqual("four", File.ReadAllText(Path.Combine(f.App, "EngineSets", "004", "llama", "cpu", "server.exe")));
    }

    [TestMethod]
    public async Task PublishedSetIdentityCannotBeReusedForDifferentBytes()
    {
        using var f = new UpdateFixture(); f.Put("EngineSets/002/llama/cpu/server.exe", "original");
        var previous = UpdateFixture.Manifest("0.5.78-beta", UpdateFixture.Entry("EngineSets/002/llama/cpu/server.exe", "original"));
        var target = UpdateFixture.Manifest("0.5.79-beta", UpdateFixture.Entry("EngineSets/002/llama/cpu/server.exe", "replacement"));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePlanner.CreateAsync(previous, target, f.Roots));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePlanner.CreateAsync(target, previous, f.Roots));
    }

    [TestMethod]
    public async Task TransitionHasEveryLegacyFileAndItsPackagesExtractIndependently()
    {
        using var f = new UpdateFixture();
        var legacy = LegacyManifest(); var legacyPath = CreateLegacy(f);
        var fresh = UpdateFixture.Manifest("0.5.78-beta", UpdateFixture.Entry("EngineSets/002/studio/native/music-server.exe", "new"));
        var output = Path.Combine(f.Folder, "packages");
        var transition = await EngineSetTransition.BuildAsync(fresh, legacy, legacyPath, output);
        EngineSetTransition.ValidatePair(transition, fresh); EngineSetTransition.ValidateLegacy(transition, legacy);
        CollectionAssert.AreEquivalent(new[] { "MusicStudioRuntime/manifest.json", "MusicStudioRuntime/music-server.exe", "MusicStudioRuntime/LICENSE" },
            transition.Files.Where(EngineSetTransition.IsLegacy).Select(file => file.Path).ToArray());
        foreach (var package in transition.Packages.Where(p => !fresh.Packages.Contains(p)))
            await UpdatePackageExtractor.ExtractAsync(Path.Combine(output, package.Id), package, transition, f.Stage);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(f.Stage, "app", "MusicStudioRuntime", "music-server.exe")));
        Assert.IsFalse(fresh.Files.Any(EngineSetTransition.IsLegacy));
        // An old updater deletes exactly the previous keys missing from its target. None may be missing from this transition.
        var oldOwned = legacy.Files.Select(file => "app/" + EngineSetTransition.LegacyPrefix + file.Path["studio/native/".Length..]);
        Assert.IsFalse(oldOwned.Except(transition.Files.Select(file => file.Key), StringComparer.Ordinal).Any());
        Assert.IsTrue(oldOwned.Except(fresh.Files.Select(file => file.Key), StringComparer.Ordinal).Any(), "Fresh is intentionally unsafe for an old worker and must be routed separately.");
    }

    [TestMethod]
    public async Task TransitionRejectsUnverifiedSourceOrIncompleteHistoricalOwnership()
    {
        using var f = new UpdateFixture(); var legacyPath = CreateLegacy(f);
        var fresh = UpdateFixture.Manifest("0.5.78-beta", UpdateFixture.Entry("EngineSets/002/studio/native/music-server.exe", "new"));
        var previous = UpdateFixture.Manifest("0.5.77-beta", UpdateFixture.Entry("MusicStudioRuntime/missing.dll", "old"));
        await Assert.ThrowsAsync<InvalidDataException>(() => EngineSetTransition.BuildAsync(fresh, LegacyManifest(), legacyPath,
            Path.Combine(f.Folder, "bad-history"), previous));
        File.WriteAllText(Path.Combine(legacyPath, "music-server.exe"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => EngineSetTransition.BuildAsync(fresh, LegacyManifest(), legacyPath,
            Path.Combine(f.Folder, "bad-content")));
    }

    [TestMethod]
    public async Task PublicationGuardRejectsMissingFreshOrIncompleteLegacyPayload()
    {
        using var f = new UpdateFixture();
        var fresh = UpdateFixture.Manifest("0.5.78-beta", UpdateFixture.Entry("EngineSets/002/studio/native/music-server.exe", "new"));
        var transition = await EngineSetTransition.BuildAsync(fresh, LegacyManifest(), CreateLegacy(f), Path.Combine(f.Folder, "packages"));
        Assert.Throws<InvalidDataException>(() => EngineSetTransition.ValidatePair(fresh, fresh));
        Assert.Throws<InvalidDataException>(() => EngineSetTransition.ValidatePair(transition, transition));
        var incomplete = transition with { Files = transition.Files.Where(file => file.Path != "MusicStudioRuntime/LICENSE").ToArray() };
        Assert.Throws<InvalidDataException>(() => EngineSetTransition.ValidateLegacy(incomplete, LegacyManifest()));
    }

    [TestMethod]
    public async Task OldClientUsesTransitionAndCurrentClientAndMiniUseFresh()
    {
        using var server = new RoutingHandler(); using var http = new HttpClient(server);
        var catalog = new PublishedUpdateCatalog(http, server.Keys);
        var old = await catalog.CheckAsync("0.5.77-beta", UpdateDelivery.FilePatch);
        var current = await catalog.CheckAsync("0.5.77-beta", UpdateDelivery.FilePatch, preferFreshManifest: true);
        var mini = await catalog.CheckAsync("0.0.0", UpdateDelivery.FilePatch, requireSetup: true);
        Assert.IsTrue(old!.Files!.Files.Any(EngineSetTransition.IsLegacy));
        Assert.IsFalse(current!.Files!.Files.Any(EngineSetTransition.IsLegacy));
        Assert.IsFalse(mini!.Files!.Files.Any(EngineSetTransition.IsLegacy));
        Assert.IsTrue(current.Files.Files.Any(EngineSetRetention.IsVersioned));
    }

    private static UpdateManifest LegacyManifest() => UpdateFixture.Manifest("0.5.78-beta",
        UpdateFixture.Entry("studio/native/manifest.json", "{}"), UpdateFixture.Entry("studio/native/music-server.exe", "old"),
        UpdateFixture.Entry("studio/native/LICENSE", "license"));
    private static string CreateLegacy(UpdateFixture f)
    {
        var directory = Path.Combine(f.Folder, "legacy"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}"); File.WriteAllText(Path.Combine(directory, "music-server.exe"), "old");
        File.WriteAllText(Path.Combine(directory, "LICENSE"), "license"); return directory;
    }
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string> { ["test"] = _key.ExportSubjectPublicKeyInfoPem() };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] bytes;
            if (request.RequestUri!.Host == "api.github.com")
                bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v0.5.78-beta", draft = false, body = "Update", published_at = "2026-10-18T00:00:00Z",
                    assets = new[] { PublishedUpdateCatalog.FileManifestName, EngineSetTransition.FreshManifestName }.Select(name => new
                    { name, size = 100, browser_download_url = "https://github.com/PiTrolKun/LOPATA/releases/download/v0.5.78-beta/" + name }) } });
            else
            {
                var files = new List<UpdateFile> { UpdateFixture.Entry("EngineSets/002/studio/native/music-server.exe", "new"),
                    UpdateFixture.Entry(BootstrapPreparation.ProtocolFile, "1") };
                if (request.RequestUri.AbsolutePath.EndsWith("/lopata-files.json", StringComparison.Ordinal))
                    files.Add(UpdateFixture.Entry("MusicStudioRuntime/music-server.exe", "old"));
                bytes = JsonSerializer.SerializeToUtf8Bytes(SignedManifest.Sign(UpdateFixture.Manifest("0.5.78-beta", files.ToArray()), "test", _key), UpdateManifest.JsonOptions);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
        protected override void Dispose(bool disposing) { if (disposing) _key.Dispose(); base.Dispose(disposing); }
    }
}
