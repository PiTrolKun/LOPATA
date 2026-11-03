using System.Security.Cryptography;
using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutGemmaAuditTests
{
    [TestMethod]
    public async Task ScenarioCardUsesCommonFirstUsePresenceFullAndReinstallationReceipts()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var receipts = Path.Combine(root, "audit");
            using var audit = ComponentAudit.UseTestStore(receipts, false);
            var card = ManagedModelCatalog.CreateLetItOut(root);
            // Small GGUF stand-in: test the same artifact identity and common audit, without allocating 14 GB.
            card.Files[0].SizeBytes = 5;
            card.Files[0].Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("valid")));
            Directory.CreateDirectory(card.InstallDirectory);
            var path = Path.Combine(card.InstallDirectory, LetItOutModel.FileName);
            await File.WriteAllTextAsync(path, "wrong");
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library")); store.Upsert(card);
            using var acquisition = new ManagedModelAcquisitionService(store);
            Assert.AreEqual(ManagedModelStatuses.Corrupted, (await acquisition.VerifyAsync(card.ModelArtifactId, null, default)).Status);
            await File.WriteAllTextAsync(path, "valid");
            var full = await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default);
            Assert.IsNotNull(full.LastVerifiedAt);
            await File.WriteAllTextAsync(path, "changed");
            var presence = await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default);
            Assert.AreEqual(full.LastVerifiedAt, presence.LastVerifiedAt);
            using (ComponentAudit.UseTestStore(receipts, true))
                await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default));
            await File.WriteAllTextAsync(path, "valid");
            await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default);
            File.Delete(path);
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default));
            await File.WriteAllTextAsync(path, "valid");
            await ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default);
            ComponentAudit.Invalidate(card.InstallDirectory);
            await File.WriteAllTextAsync(path, "wrong");
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => ManagedModelRuntimeAudit.VerifyAsync(store, card.ModelArtifactId, default));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }
}
