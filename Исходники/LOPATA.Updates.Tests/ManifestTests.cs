using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class ManifestTests
{
    [TestMethod]
    public void SignatureRejectsTamperingWrongKeyAndUnknownSigner()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["release-1"] = key.ExportSubjectPublicKeyInfoPem() };
        var manifest = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "valid"));
        var signed = SignedManifest.Sign(manifest, "release-1", key);
        Assert.AreEqual("0.2.42-beta", signed.Verify(keys).Version);
        Assert.Throws<InvalidDataException>(() => (signed with { KeyId = "unknown" }).Verify(keys));
        Assert.Throws<InvalidDataException>(() => signed.Verify(new Dictionary<string, string>
            { ["release-1"] = other.ExportSubjectPublicKeyInfoPem() }));
        var modified = Encoding.UTF8.GetString(Convert.FromBase64String(signed.Payload)).Replace("0.2.42", "0.2.43");
        Assert.Throws<InvalidDataException>(() => (signed with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(modified)) }).Verify(keys));
        var roundtrip = SignedManifest.Read(JsonSerializer.SerializeToUtf8Bytes(signed, UpdateManifest.JsonOptions));
        Assert.AreEqual(manifest.Version, roundtrip.Verify(keys).Version);
    }

    [TestMethod]
    public void DuplicateJsonCannotShadowSignatureOrManifestFields()
    {
        Assert.Throws<InvalidDataException>(() => SignedManifest.Read(Encoding.UTF8.GetBytes(
            "{\"keyId\":\"first\",\"keyId\":\"second\",\"payload\":\"\",\"signature\":\"\"}")));
    }

    [TestMethod]
    public void ManifestRejectsDuplicatePathsParentCollisionsUnknownRootsAndBadVersions()
    {
        var file = UpdateFixture.Entry("AIHub.exe", "file");
        var manifest = UpdateFixture.Manifest("0.2.42-beta", file);
        manifest.Validate();
        foreach (var bad in new[]
        {
            manifest with { Files = [file, file with { Path = "aihub.EXE" }] },
            manifest with { Files = [file, file with { Path = "AIHub.exe/child" }] },
            manifest with { Files = [file with { Root = "userdata" }] },
            manifest with { Version = "0.2.42-dev" },
            manifest with { Version = "0.2.42-beta.2" },
            manifest with { MinimumUpdaterVersion = 2 },
            manifest with { Notes = [] },
            manifest with { Packages = [manifest.Packages[0] with { Url = "https://github.com.evil.test/files.zip" }] }
        }) Assert.Throws<InvalidDataException>(bad.Validate);
    }

    [TestMethod]
    [DataRow("../user.txt")]
    [DataRow("sub/../../user.txt")]
    [DataRow("C:/user.txt")]
    [DataRow("/user.txt")]
    [DataRow("folder\\file.txt")]
    [DataRow("file:stream")]
    [DataRow("name. /file")]
    [DataRow("CON.txt")]
    [DataRow("sub/LPT1.log")]
    [DataRow(".lopata-update/file")]
    [DataRow("Models/model.gguf")]
    [DataRow("settings.json")]
    [DataRow("unins000.exe")]
    public void UnsafeWindowsPathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => SafeUpdatePath.ValidateRelative(path));

    [TestMethod]
    public void InstallationRootsCannotBeNestedOrDiskRoot()
    {
        using var fixture = new UpdateFixture();
        Assert.Throws<InvalidDataException>(() => new UpdateRoots(new Dictionary<string, string>
            { ["app"] = fixture.App, ["llama"] = Path.Combine(fixture.App, "backend") }));
        Assert.Throws<InvalidDataException>(() => new UpdateRoots(new Dictionary<string, string>
            { ["app"] = Path.GetPathRoot(fixture.App)! }));
    }
}
