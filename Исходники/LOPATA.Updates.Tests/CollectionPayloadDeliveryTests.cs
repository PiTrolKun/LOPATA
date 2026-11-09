using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class CollectionPayloadDeliveryTests
{
    private const string Old = "Collection/0123456789abcdef01234567/LOPATA.CollectionShell.dll";
    private const string Next = "Collection/abcdef0123456789abcdef01-a1b2c3d4/LOPATA.CollectionShell.dll";

    [TestMethod]
    public async Task OldLoadedGenerationIsNotDeletedButOrdinaryObsoleteFilesStillAre()
    {
        using var files = new UpdateFixture(); files.Put(Old, "old"); files.Put("obsolete.dll", "old");
        var before = UpdateFixture.Manifest("0.7.64-beta", UpdateFixture.Entry(Old, "old"), UpdateFixture.Entry("obsolete.dll", "old"));
        var after = UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry(Next, "new"));
        var plan = await UpdatePlanner.CreateAsync(before, after, files.Roots);
        CollectionAssert.AreEqual(new[] { "obsolete.dll" }, plan.Operations.Where(o => o.Action == UpdateAction.Delete).Select(o => o.File.Path).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePlanner.CreateAsync(before,
            UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry(Old, "different")), files.Roots));
    }

    [TestMethod]
    [DataRow("Collection/not-a-generation/library.dll", "app")]
    [DataRow("Collection/0123456789abcdef01234567dead/library.dll", "app")]
    [DataRow("Collection/ABCDEF0123456789abcdef01/library.dll", "app")]
    [DataRow("Collection/0123456789abcdef01234567/../user.txt", "app")]
    [DataRow("Collection/0123456789abcdef01234567/library.dll", "llama")]
    [DataRow("Collection/current.json", "app")]
    public void RetentionAppliesOnlyToTheExactApplicationPayloadScope(string path, string root)
    {
        Assert.IsFalse(CollectionPayloadRetention.IsVersioned(UpdateFixture.Entry(path, "bytes") with { Root = root }));
        Assert.IsTrue(CollectionPayloadRetention.IsVersioned(UpdateFixture.Entry(Old, "bytes")));
        Assert.IsTrue(CollectionPayloadRetention.IsVersioned(UpdateFixture.Entry(Next, "bytes")));
    }

    [TestMethod]
    public async Task UninstallRemembersAndSafelyRemovesRetiredGenerationsAcrossAnUpdate()
    {
        using var files = new UpdateFixture(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        files.Put(Old, "old"); files.Put("user.txt", "private");
        var keys = new Dictionary<string, string> { ["fixture"] = key.ExportSubjectPublicKeyInfoPem() };
        var state = Path.Combine(files.Folder, "state"); var transaction = new UpdateTransaction(files.Roots, keys, state);
        var before = SignedManifest.Sign(UpdateFixture.Manifest("0.7.64-beta", UpdateFixture.Entry(Old, "old")), "fixture", key);
        await transaction.RegisterInstalledAsync(before);
        var (package, manifest) = files.Zip(new() { ["app/" + Next] = "new" }, UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry(Next, "new")));
        await UpdatePackageExtractor.ExtractAsync(package, manifest.Packages[0], manifest, files.Stage);
        var after = SignedManifest.Sign(manifest, "fixture", key);
        var id = await transaction.PrepareAsync(before, after, files.Stage);
        await transaction.ApplyAsync(id); await transaction.ConfirmHealthyAsync(id);
        Assert.IsTrue(File.Exists(Path.Combine(files.App, Old)));
        await new UpdateTransaction(files.Roots, keys, state).RemoveManagedFilesAsync();
        Assert.IsFalse(File.Exists(Path.Combine(files.App, Old)));
        Assert.IsFalse(File.Exists(Path.Combine(files.App, Next)));
        Assert.AreEqual("private", File.ReadAllText(Path.Combine(files.App, "user.txt")));
    }

    [TestMethod]
    public async Task UninstallPreservesLockedOrChangedPayloadAndRecordsItsSignedOwnership()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing semantics required.");
        using var files = new UpdateFixture(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        files.Put(Old, "original"); files.Put(Next, "original"); files.Put("ordinary.dll", "bytes");
        var keys = new Dictionary<string, string> { ["fixture"] = key.ExportSubjectPublicKeyInfoPem() };
        var state = Path.Combine(files.Folder, "state"); var transaction = new UpdateTransaction(files.Roots, keys, state);
        await transaction.RegisterInstalledAsync(SignedManifest.Sign(UpdateFixture.Manifest("0.7.65-beta",
            UpdateFixture.Entry(Old, "original"), UpdateFixture.Entry(Next, "original"), UpdateFixture.Entry("ordinary.dll", "bytes")), "fixture", key));
        files.Put(Next, "user modified this component");
        using (var held = new FileStream(Path.Combine(files.App, Old), FileMode.Open, FileAccess.Read, FileShare.Read))
            await transaction.RemoveManagedFilesAsync();
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(files.App, Old)));
        Assert.AreEqual("user modified this component", File.ReadAllText(Path.Combine(files.App, Next)));
        Assert.IsFalse(File.Exists(Path.Combine(files.App, "ordinary.dll")));
        Assert.IsFalse(File.Exists(transaction.InstalledManifestPath));
        using var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "collection-retained.json")));
        Assert.HasCount(2, log.RootElement.EnumerateArray());
        Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(state, "collection-ownership"), "*.signed.json"));
    }

    [TestMethod]
    public async Task UninstallStillFailsOnAnOrdinaryLockedManagedFile()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing semantics required.");
        using var files = new UpdateFixture(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        files.Put("ordinary.dll", "bytes");
        var keys = new Dictionary<string, string> { ["fixture"] = key.ExportSubjectPublicKeyInfoPem() };
        var transaction = new UpdateTransaction(files.Roots, keys, Path.Combine(files.Folder, "state"));
        await transaction.RegisterInstalledAsync(SignedManifest.Sign(UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry("ordinary.dll", "bytes")), "fixture", key));
        using var held = new FileStream(Path.Combine(files.App, "ordinary.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => transaction.RemoveManagedFilesAsync());
        Assert.IsTrue(File.Exists(transaction.InstalledManifestPath));
    }
}
