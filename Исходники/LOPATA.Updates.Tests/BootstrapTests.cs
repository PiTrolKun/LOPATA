using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class BootstrapTests
{
    [TestMethod]
    public void NativeSerializationPreservesExistingSignedPayloadFormat()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var target = UpdateFixture.Manifest("0.5.1-beta", UpdateFixture.Entry("sample.txt", "Русский"));
        var signed = SignedManifest.Sign(target, "test", key);
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(target, UpdateManifest.JsonOptions), Convert.FromBase64String(signed.Payload));
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        Assert.AreEqual(target.Version, SignedManifest.Read(JsonSerializer.SerializeToUtf8Bytes(signed, UpdateManifest.JsonOptions)).Verify(keys).Version);
    }

    [TestMethod]
    public async Task StarterVerifiesEngineAndProtocolBeforeReturningExecutable()
    {
        using var fixture = new UpdateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var entries = Entries("1");
        var (zip, target) = fixture.Zip(entries, UpdateFixture.Manifest("0.5.1-beta",
            entries.Select(e => UpdateFixture.Entry(e.Key[4..], e.Value)).ToArray()));
        using var handler = new ParallelDownloadHandler(new() { [target.Packages[0].Id] = File.ReadAllBytes(zip) });
        using var http = new HttpClient(handler);
        var starter = new BootstrapPreparation(http, keys) { MaximumParallelConnections = 4 };
        var signed = SignedManifest.Sign(target, "test", key);
        var engine = await starter.PrepareAsync(signed, Path.Combine(fixture.Folder, "cache"), fixture.Stage);
        Assert.AreEqual("engine", File.ReadAllText(engine));
        var requests = handler.Requests.Count;
        await starter.PrepareAsync(signed, Path.Combine(fixture.Folder, "cache"), fixture.Stage);
        Assert.AreEqual(requests, handler.Requests.Count);
        Assert.ThrowsExactly<InvalidDataException>(() => BootstrapPreparation.RequiredFiles(target with
            { Files = target.Files.Where(f => f.Path != BootstrapPreparation.ProtocolFile).ToArray() }));
        entries["app/" + BootstrapPreparation.ProtocolFile] = "2";
        var (otherZip, other) = fixture.Zip(entries, UpdateFixture.Manifest("0.5.2-beta",
            entries.Select(e => UpdateFixture.Entry(e.Key[4..], e.Value)).ToArray()));
        using var otherHandler = new ParallelDownloadHandler(new() { [other.Packages[0].Id] = File.ReadAllBytes(otherZip) });
        using var otherHttp = new HttpClient(otherHandler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BootstrapPreparation(otherHttp, keys)
            .PrepareAsync(SignedManifest.Sign(other, "test", key), Path.Combine(fixture.Folder, "other-cache"), fixture.Stage));
    }

    [TestMethod]
    public async Task UniversalSetupUpgradesRegisteredFilesAndPreservesUnknownData()
    {
        using var fixture = new UpdateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var old = UpdateFixture.Manifest("0.5.0-beta", UpdateFixture.Entry("AIHub.exe", "old"));
        fixture.Put("AIHub.exe", "old"); fixture.Put("model.bin", "my model");
        File.WriteAllText(Path.Combine(fixture.App, "settings.json"), "my settings");
        var state = Path.Combine(fixture.Folder, "state");
        var transaction = new UpdateTransaction(fixture.Roots, keys, state);
        await transaction.RegisterInstalledAsync(SignedManifest.Sign(old, "test", key));
        var entries = Entries("1"); entries.Add("app/AIHub.exe", "new");
        var (zip, target) = fixture.Zip(entries, UpdateFixture.Manifest("0.5.1-beta",
            entries.Select(e => UpdateFixture.Entry(e.Key[4..], e.Value)).ToArray()));
        using var handler = new ParallelDownloadHandler(new() { [target.Packages[0].Id] = File.ReadAllBytes(zip) });
        using var http = new HttpClient(handler);
        var setup = new SetupInstallation(http, keys);
        var signed = SignedManifest.Sign(target, "test", key);
        var cache = Path.Combine(fixture.Folder, "cache");
        await setup.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed);
        await setup.InstallAsync(fixture.Roots, state, cache, fixture.Stage, signed);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.AreEqual("my model", File.ReadAllText(Path.Combine(fixture.App, "model.bin")));
        Assert.AreEqual("my settings", File.ReadAllText(Path.Combine(fixture.App, "settings.json")));
        await transaction.RemoveManagedFilesAsync();
        Assert.IsFalse(File.Exists(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.AreEqual("my model", File.ReadAllText(Path.Combine(fixture.App, "model.bin")));
    }

    private static Dictionary<string, string> Entries(string protocol) => new()
    {
        ["app/Updater/LOPATA.Updater.exe"] = "engine", ["app/Licenses/installer.txt"] = "license",
        ["app/Licenses/installer-receipt.json"] = "receipt", ["app/" + BootstrapPreparation.ProtocolFile] = protocol
    };
}
