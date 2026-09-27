using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class PreparedUpdateTests
{
    [TestMethod]
    public void DirectionStartsUnselectedAndDoesNotChangePendingRelease()
    {
        using var fixture = new UpdateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var manifest = UpdateFixture.Manifest("0.2.43-beta", UpdateFixture.Entry("AIHub.exe", "new"));
        var store = new PreparedUpdateStore(fixture.Folder, keys);
        var pending = new PreparedApplicationUpdate(manifest.Version, UpdateDelivery.FilePatch, null, null,
            SignedManifest.Sign(manifest, "test", key), false, manifest.Notes);
        Assert.IsNull(UpdateChannelStore.Read(fixture.Folder));
        store.Save(pending);
        UpdateChannelStore.Save(fixture.Folder, UpdateDelivery.FullInstaller);
        Assert.AreEqual(UpdateDelivery.FullInstaller, UpdateChannelStore.Read(fixture.Folder));
        Assert.AreEqual(pending.Version, store.Read()!.Version);
        Assert.AreEqual(UpdateDelivery.FilePatch, store.Read()!.Delivery);
        Assert.IsFalse(store.Read()!.ApplyOnNextLaunch);
        store.Save(pending with { ApplyOnNextLaunch = true });
        var reopened = new PreparedUpdateStore(fixture.Folder, keys).Read()!;
        Assert.IsTrue(reopened.ApplyOnNextLaunch);
        CollectionAssert.AreEqual(manifest.Notes, reopened.Notes!);
        UpdateChannelStore.Save(fixture.Folder, UpdateDelivery.FilePatch);
        Assert.AreEqual(manifest.Version, store.Read()!.Version);
        store.Clear();
        Assert.IsNull(store.Read());
        Assert.AreEqual(UpdateDelivery.FilePatch, UpdateChannelStore.Read(fixture.Folder));
    }

    [TestMethod]
    public void PendingTargetAndDeliveryCannotBeSubstituted()
    {
        using var fixture = new UpdateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var manifest = UpdateFixture.Manifest("0.2.43-beta", UpdateFixture.Entry("AIHub.exe", "new"));
        var pending = new PreparedApplicationUpdate(manifest.Version, UpdateDelivery.FilePatch, null, null,
            SignedManifest.Sign(manifest, "test", key), false);
        var store = new PreparedUpdateStore(fixture.Folder, keys);
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(pending with { Version = "0.2.44-beta" }));
        Assert.ThrowsExactly<InvalidDataException>(() => store.Save(pending with { Delivery = UpdateDelivery.FullInstaller }));
        Assert.IsNull(store.Read());
    }
}
