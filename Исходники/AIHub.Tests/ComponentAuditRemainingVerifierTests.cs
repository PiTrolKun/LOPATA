using System.Security.Cryptography;
using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ComponentAuditRemainingVerifierTests
{
    [TestMethod]
    public async Task LiteraryArtifactFirstUseIsFullAndOffReuseDoesNotInspectPayloadMetadata()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "audit"), false);
        var directory = Path.Combine(files.Root, "model"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "model.bin"); await File.WriteAllTextAsync(path, "original");
        var verifier = new LiteraryFileVerification(Path.Combine(files.Root, "legacy-receipts"));
        Assert.IsFalse(await verifier.ValidAsync(path, 8, Hash("another!"), "sha256", default));
        Assert.IsTrue(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));

        await File.WriteAllTextAsync(path, "modified with a different length");
        Assert.IsTrue(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));
        Assert.IsFalse(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default, force: true));
        Assert.IsFalse(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));

        await File.WriteAllTextAsync(path, "original");
        Assert.IsTrue(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));
        await File.WriteAllTextAsync(path, "modified");
        ComponentAudit.Invalidate(directory);
        Assert.IsFalse(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));
        File.Delete(path);
        Assert.IsFalse(await verifier.ValidAsync(path, 8, Hash("original"), "sha256", default));
    }

    [TestMethod]
    public async Task ManagedRuntimeUseSharesThePreparationProofAndRechecksAReplacedInstallation()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "audit"), false);
        var root = Path.Combine(files.Root, "models"); var directory = Path.Combine(root, "runtime-use");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "weights.bin"); await File.WriteAllTextAsync(path, "original");
        var store = new ManagedModelLibraryStore(Path.Combine(files.Root, "library"));
        var card = new ManagedModelArtifactCard { ModelArtifactId = "audit-runtime-use", DisplayName = "Audit runtime test",
            IsManaged = true, ModelsRoot = root, InstallDirectory = directory, Revision = "fixed-revision", Role = ManagedModelRoles.Tool,
            Files = [new() { RelativePath = "weights.bin", SizeBytes = 8, Sha256 = Hash("original") }] };
        store.Upsert(card);
        using (var downloads = new ManagedModelAcquisitionService(store))
            Assert.AreEqual(ManagedModelStatuses.Installed, (await downloads.VerifyAsync(card.ModelArtifactId, null, default)).Status);

        await File.WriteAllTextAsync(path, "changed and longer");
        Assert.AreEqual(ManagedModelStatuses.Installed, (await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default)).Status);
        ComponentAudit.Invalidate(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
