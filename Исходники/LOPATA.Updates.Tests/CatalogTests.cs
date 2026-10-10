using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class CatalogTests
{
    [TestMethod]
    public async Task BothDirectionsShareVersionsButDiscoverDifferentArtifactTypes()
    {
        using var fixture = new CatalogFixture();
        var catalog = new PublishedUpdateCatalog(fixture.Http, fixture.Keys);
        var full = await catalog.CheckAsync("0.2.42-beta", UpdateDelivery.FullInstaller);
        var patch = await catalog.CheckAsync("0.2.42-beta", UpdateDelivery.FilePatch);
        Assert.AreEqual("0.2.43-beta", full!.Version, "Stable delivery must accept public beta-numbered full installers.");
        Assert.IsNotNull(full.Installer);
        Assert.AreEqual("0.2.44-beta", patch!.Version);
        Assert.IsNull(patch.Installer);
        Assert.AreEqual(2, patch.Notes.Length);
        Assert.AreEqual("0.2.43-beta", patch.Notes[0].Version);
        Assert.AreEqual("0.2.44-beta", patch.Notes[1].Version);
        Assert.IsFalse(fixture.Requests.Any(u => u.EndsWith(".zip") || u.EndsWith(".exe")), "Discovery downloads metadata only.");
    }

    [TestMethod]
    public async Task SelectingInstallerDirectionNeverOffersDowngradeOrSameNumberPromotion()
    {
        using var fixture = new CatalogFixture();
        var catalog = new PublishedUpdateCatalog(fixture.Http, fixture.Keys);
        Assert.IsNull(await catalog.CheckAsync("0.2.44-beta", UpdateDelivery.FullInstaller));
        Assert.IsNull(await catalog.CheckAsync("0.2.44-dev", UpdateDelivery.FilePatch));
    }

    [TestMethod]
    public async Task CorruptSignedMetadataIsRejectedBeforeDownloadingPayload()
    {
        using var fixture = new CatalogFixture();
        fixture.Corrupt = true;
        var catalog = new PublishedUpdateCatalog(fixture.Http, fixture.Keys);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.CheckAsync("0.2.42-beta", UpdateDelivery.FilePatch));
        Assert.IsFalse(fixture.Requests.Any(u => u.EndsWith(".zip")));
    }

    [TestMethod]
    public async Task UniversalInstallerSelectsLatestCompatibleSignedPublishedRelease()
    {
        using var fixture = new CatalogFixture { SetupCompatible = true };
        var offer = await new PublishedUpdateCatalog(fixture.Http, fixture.Keys)
            .CheckAsync("0.0.0", UpdateDelivery.FilePatch, requireSetup: true);
        Assert.AreEqual("0.2.43-beta", offer!.Version, "A newer incompatible protocol must not select an unsupported engine.");
        Assert.IsFalse(fixture.Requests.Any(u => u.EndsWith(".zip")));
    }

    private sealed class CatalogFixture : HttpMessageHandler
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public HttpClient Http { get; }
        public bool Corrupt;
        public bool SetupCompatible;
        public List<string> Requests { get; } = [];
        public IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string> { ["test"] = _key.ExportSubjectPublicKeyInfoPem() };
        public CatalogFixture() => Http = new HttpClient(this, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            byte[] payload;
            if (request.RequestUri.Host == "api.github.com")
            {
                object Release(string version, bool full) => new
                {
                    tag_name = "v" + version, draft = false, prerelease = true, published_at = "2026-09-27T00:00:00Z", body = version,
                    assets = new[] { "lopata-files.json" }.Concat(full ? new[] { "lopata-update.json", $"LOPATA_Setup_{version}.exe" } : [])
                        .Select(name => new { name, size = 100, browser_download_url = $"https://github.com/PiTrolKun/LOPATA/releases/download/v{version}/{name}" })
                };
                payload = JsonSerializer.SerializeToUtf8Bytes(new[] { Release("0.2.44-beta", false), Release("0.2.43-beta", true) });
            }
            else if (url.EndsWith("lopata-update.json", StringComparison.Ordinal))
                payload = JsonSerializer.SerializeToUtf8Bytes(new { version = "0.2.43-beta", fileName = "LOPATA_Setup_0.2.43-beta.exe", size = 100, sha256 = new string('a', 64) });
            else
            {
                var version = url.Contains("v0.2.44-beta") ? "0.2.44-beta" : "0.2.43-beta";
                var manifest = UpdateFixture.Manifest(version, UpdateFixture.Entry("AIHub.exe", "binary"));
                if (SetupCompatible && version == "0.2.43-beta")
                    manifest = UpdateFixture.Manifest(version, UpdateFixture.Entry("AIHub.exe", "binary"),
                        UpdateFixture.Entry(BootstrapPreparation.ProtocolFile, "1"));
                if (version == "0.2.44-beta") manifest = manifest with { Notes = [new("0.2.43-beta", DateTimeOffset.UtcNow, "First"), .. manifest.Notes] };
                var signed = SignedManifest.Sign(manifest, "test", _key);
                if (Corrupt) signed = signed with { Signature = Convert.ToBase64String(new byte[64]) };
                payload = JsonSerializer.SerializeToUtf8Bytes(signed, UpdateManifest.JsonOptions);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Http.Dispose(); _key.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
