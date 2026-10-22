using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIHub.Services;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ComponentAuditDomainTests
{
    [TestMethod]
    public async Task DirectRuntimeFirstUseRejectsCorruptionThenPresenceDoesNotReadContentOrCheckSize()
    {
        using var files = new MusicProjectTests.Files();
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "required.dll");
        var pins = new[] { Pin("required.dll", "original") };
        var receipts = Path.Combine(files.Root, "receipts");
        using (ComponentAudit.UseTestStore(receipts, false))
        {
            await File.WriteAllTextAsync(path, "modified");
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", pins, default));
            await File.WriteAllTextAsync(path, "original");
            await RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", pins, default);
            await File.WriteAllTextAsync(path, "different content and size");
            // Exclusive access proves the quick pass does not open the required payload for reading.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", pins, default);
            File.Delete(path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", pins, default));
            await File.WriteAllTextAsync(path, "modified");
        }
        using (ComponentAudit.UseTestStore(receipts, true))
            await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", pins, default));
    }

    [TestMethod]
    public async Task PinnedMetadataOrInstallationChangeMakesDirectRuntimeFirstUseMandatoryAgain()
    {
        using var files = new MusicProjectTests.Files();
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "required.dll");
        using var audit = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        await File.WriteAllTextAsync(path, "original");
        await RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", [Pin("required.dll", "original")], default);
        await File.WriteAllTextAsync(path, "modified");
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", [Pin("required.dll", "new pins")], default));
        ComponentAudit.Invalidate(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", [Pin("required.dll", "original")], default));
    }

    [TestMethod]
    public async Task DirectRuntimeStillRejectsUnsafeRequiredPathsInPresenceMode()
    {
        using var files = new MusicProjectTests.Files();
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        using var audit = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        await File.WriteAllTextAsync(Path.Combine(root, "required.dll"), "original");
        await RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", [Pin("required.dll", "original")], default);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.VerifyAsync("test.runtime", root, "1", [Pin("../required.dll", "original")], default));
    }

    [TestMethod]
    public async Task DerivedOverlayInventoryIsIsolatedAndRequiresAllFilesWithoutOpeningPayloads()
    {
        using var files = new MusicProjectTests.Files();
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "required.dll"); await File.WriteAllTextAsync(path, "payload");
        var store = Path.Combine(files.Root, "audit");
        using var audit = ComponentAudit.UseTestStore(store, false);
        await RuntimeFileAudit.SaveInventoryAsync(root, "1", ["required.dll"], default);
        StringAssert.StartsWith(RuntimeFileAudit.InventoryPath(root, "1"), store + Path.DirectorySeparatorChar);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await RuntimeFileAudit.PresenceInventoryAsync(root, "1", default);
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RuntimeFileAudit.PresenceInventoryAsync(root, "1", default));
        await RuntimeFileAudit.SaveInventoryAsync(root, "1", ["../required.dll"], default);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeFileAudit.PresenceInventoryAsync(root, "1", default));
    }

    [TestMethod]
    public void PreparedBookSessionInQuickModeUsesOnlyKnownFilePresence()
    {
        using var files = new MusicProjectTests.Files();
        Directory.CreateDirectory(files.Root);
        var model = Path.Combine(files.Root, "model.bin"); File.WriteAllText(model, "original");
        using var audit = ComponentAudit.UseTestStore(Path.Combine(files.Root, "audit"), false);
        var selection = new BookSmartSelection();
        var prepared = new BookSmartPreparedSession(files.Root, selection, true, "002", [model]);
        File.WriteAllText(model, "changed content and size");
        File.SetLastWriteTimeUtc(model, DateTime.UtcNow.AddSeconds(10));
        File.WriteAllText(Path.Combine(files.Root, "extra.cache"), "unrelated cache");
        using (File.Open(model, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.IsTrue(prepared.Matches(files.Root, selection, true, "002", [model]));
        using (ComponentAudit.ForceFull())
            Assert.IsFalse(prepared.Matches(files.Root, selection, true, "002", [model]));
        File.Delete(model);
        Assert.IsFalse(prepared.Matches(files.Root, selection, true, "002", [model]));
    }

    private static PinnedTreeFile Pin(string path, string content) => new(path, Encoding.UTF8.GetByteCount(content),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
}
