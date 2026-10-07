using System.IO;
using AIHub.Services;
using AIHub.Models;

namespace AIHub.Tests;

[TestClass]
public sealed class ManagedModelPathIdentityTests
{
    [TestMethod]
    public void EquivalentPathSpellingPreservesVerifiedReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-path-identity-" + Guid.NewGuid().ToString("N"));
        var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
        var installedAt = DateTime.UtcNow;
        ManagedModelArtifactCard Card(string directory) => new()
        {
            ModelArtifactId = "path-test", InstallDirectory = directory,
            Files = [new() { RelativePath = "model.gguf", SizeBytes = 123, Sha256 = new string('a', 64) }]
        };
        var initial = Card(Path.Combine(root, "weights"));
        initial.Status = ManagedModelStatuses.Installed;
        initial.Files[0].VerifiedSizeBytes = 123;
        initial.Files[0].VerifiedLastWriteTimeUtc = installedAt;
        initial.LastVerifiedAt = DateTimeOffset.UtcNow;
        store.Upsert(initial);
        var equivalent = Card(initial.InstallDirectory.Replace('\\', '/') + "/");
        equivalent.Status = ManagedModelStatuses.Installed;
        var saved = store.Upsert(equivalent);
        Assert.AreEqual(123L, saved.Files[0].VerifiedSizeBytes);
        Assert.AreEqual(installedAt, saved.Files[0].VerifiedLastWriteTimeUtc);
        Assert.IsNotNull(saved.LastVerifiedAt);
        var moved = Card(Path.Combine(root, "different"));
        moved.Status = ManagedModelStatuses.Installed;
        var changed = store.Upsert(moved);
        Assert.AreEqual(ManagedModelStatuses.NeedsVerification, changed.Status);
        Assert.AreEqual(0L, changed.Files[0].VerifiedSizeBytes);
        Assert.IsNull(changed.LastVerifiedAt);
    }
}
