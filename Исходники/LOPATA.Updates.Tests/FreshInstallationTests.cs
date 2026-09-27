using System.Net;
using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class FreshInstallationTests
{
    [TestMethod]
    public async Task NetworkInstallationUsesVerifiedPackagesAndKeepsUnknownFiles()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("personal.txt", "keep");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var (zip, manifest) = fixture.Zip(new() { ["app/AIHub.exe"] = "payload", ["app/AIHub.exe.extracting"] = "also a real file" },
            UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "payload"), UpdateFixture.Entry("AIHub.exe.extracting", "also a real file")));
        var signed = SignedManifest.Sign(manifest, "test", key);
        var handler = new PackageHandler(File.ReadAllBytes(zip));
        using var http = new HttpClient(handler);
        var state = Path.Combine(fixture.Folder, "state");
        var installer = new FreshInstallation(http, keys);
        await installer.InstallAsync(fixture.Roots, state, Path.Combine(fixture.Folder, "cache"), fixture.Stage, signed);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.AreEqual("also a real file", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe.extracting")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.App, "personal.txt")));
        Assert.AreEqual(UpdatePhase.Healthy, new UpdateTransaction(fixture.Roots, keys, state).ReadJournal()!.Phase);
        await installer.InstallAsync(fixture.Roots, state, Path.Combine(fixture.Folder, "cache"), fixture.Stage, signed);
        Assert.AreEqual(1, handler.Calls, "Interrupted Inno registration must resume without another download.");
        var other = SignedManifest.Sign(manifest with { Version = "0.2.43-beta",
            Notes = manifest.Notes.Select(n => n with { Version = "0.2.43-beta" }).ToArray() }, "test", key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(fixture.Roots, state,
            Path.Combine(fixture.Folder, "cache"), fixture.Stage, other));
        File.WriteAllText(Path.Combine(fixture.App, "AIHub.exe"), "local edit");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(fixture.Roots, state,
            Path.Combine(fixture.Folder, "cache"), fixture.Stage, signed));
        Assert.AreEqual("local edit", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")));
    }

    [TestMethod]
    public async Task NetworkFailureBeforeApplyLeavesInstalledFolderUntouchedAndCanResume()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("personal.txt", "keep");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var (zip, manifest) = fixture.Zip(new() { ["app/AIHub.exe"] = "payload" },
            UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "payload")));
        var handler = new PackageHandler(File.ReadAllBytes(zip)) { Offline = true };
        using var http = new HttpClient(handler);
        var installer = new FreshInstallation(http, keys);
        var signed = SignedManifest.Sign(manifest, "test", key);
        var state = Path.Combine(fixture.Folder, "state");
        var cache = Path.Combine(fixture.Folder, "cache");
        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.App, "personal.txt")));
        handler.Offline = false;
        await installer.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed);
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")));
    }

    [TestMethod]
    public async Task UninstallForgetsOwnershipAndAllowsFreshInstallationAgain()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("personal.txt", "keep");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var (zip, manifest) = fixture.Zip(new() { ["app/AIHub.exe"] = "payload" },
            UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "payload")));
        var handler = new PackageHandler(File.ReadAllBytes(zip));
        using var http = new HttpClient(handler);
        var installer = new FreshInstallation(http, keys);
        var signed = SignedManifest.Sign(manifest, "test", key);
        var state = Path.Combine(fixture.Folder, "state");
        var cache = Path.Combine(fixture.Folder, "cache");
        await installer.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed);
        var engine = new UpdateTransaction(fixture.Roots, keys, state);
        await engine.RemoveManagedFilesAsync();
        Assert.IsFalse(File.Exists(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.IsFalse(File.Exists(engine.InstalledManifestPath));
        Assert.IsNull(engine.ReadJournal());
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.App, "personal.txt")));
        await installer.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed);
        Assert.AreEqual(1, handler.Calls, "Verified cached package survives uninstall and can be reused.");
    }

    private sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Offline) throw new HttpRequestException("Stand network interruption");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
