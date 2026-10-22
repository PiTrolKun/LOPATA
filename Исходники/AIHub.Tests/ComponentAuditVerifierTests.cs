using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class ComponentAuditVerifierTests
{
    [TestMethod]
    public async Task PinnedTreeFirstCheckIsFullAndSubsequentPresenceDoesNotInspectOrQuarantineExtras()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.py"); await File.WriteAllTextAsync(path, "original");
        var expected = new[] { Entry("a.py", "original") };
        await PinnedFileTreeVerifier.VerifyAsync(root, expected, default);

        await File.WriteAllTextAsync(path, "modified");
        await File.WriteAllTextAsync(Path.Combine(root, "unlisted.bin"), "extra");
        var cache = Path.Combine(root, "__pycache__"); Directory.CreateDirectory(cache);
        var cachedFile = Path.Combine(cache, "a.cpython-312.pyc");
        await File.WriteAllTextAsync(cachedFile, "cached");
        var quarantine = Path.Combine(files.Root, "quarantine");
        await PinnedFileTreeVerifier.VerifyAsync(root, expected, default, pythonCacheQuarantineDirectory: quarantine);
        Assert.IsTrue(File.Exists(cachedFile));
        Assert.IsFalse(Directory.Exists(quarantine));

        using (ComponentAudit.ForceFull())
            await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, expected, default));
        File.Delete(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, expected, default));
    }

    [TestMethod]
    public async Task InstallationCheckCannotReplaceFirstUseAndReinstallationInvalidatesSameRevision()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "a.py"); await File.WriteAllTextAsync(path, "original");
        var expected = new[] { Entry("a.py", "original") };
        using (ComponentAudit.Installation())
            await PinnedFileTreeVerifier.VerifyAsync(root, expected, default);
        await File.WriteAllTextAsync(path, "modified");
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, expected, default));

        await File.WriteAllTextAsync(path, "original");
        await PinnedFileTreeVerifier.VerifyAsync(root, expected, default);
        await File.WriteAllTextAsync(path, "modified");
        await PinnedFileTreeVerifier.VerifyAsync(root, expected, default);
        ComponentAudit.Invalidate(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileTreeVerifier.VerifyAsync(root, expected, default));
    }

    [TestMethod]
    public async Task PresenceLayoutDoesNotApproveTheFirstUseOfAnUntrustedNativeManifest()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        var root = Path.Combine(files.Root, "runtime"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "tool.exe"), "short");
        await File.WriteAllTextAsync(Path.Combine(root, "runtime-manifest.json"), JsonSerializer.Serialize(new
        {
            backend = "cpu", revision = NativeRuntimeReleasePolicy.UpdatedLlamaRevision,
            files = new[] { new { path = "tool.exe", size = 1000, sha256 = new string('A', 64) } }
        }));

        Assert.IsTrue(NativeEngineBundleVerifier.HasCompleteLayout("llama", "cpu", root));
        Assert.IsTrue(HardwareRuntimeBundleVerifier.HasCompleteLayout(HardwareRuntimeCatalog.LlamaCpuId, root));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeEngineBundleVerifier.VerifyFilesAsync("llama", "cpu", root, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => HardwareRuntimeBundleVerifier.VerifyFilesAsync(HardwareRuntimeCatalog.LlamaCpuId, root, default));
        using (ComponentAudit.ForceFull())
        {
            Assert.IsFalse(NativeEngineBundleVerifier.HasCompleteLayout("llama", "cpu", root));
            Assert.IsFalse(HardwareRuntimeBundleVerifier.HasCompleteLayout(HardwareRuntimeCatalog.LlamaCpuId, root));
        }
    }

    [TestMethod]
    public async Task ExpandedZipPresenceUsesRequiredEntryNamesWithoutHashingExpandedPayloadsOrWalkingExtras()
    {
        using var files = new MusicProjectTests.Files();
        using var policy = ComponentAudit.UseTestStore(Path.Combine(files.Root, "receipts"), false);
        var root = Path.Combine(files.Root, "expanded"); Directory.CreateDirectory(Path.Combine(root, "bin"));
        var source = Path.Combine(files.Root, "source.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("bin/tool.exe").Open(), new UTF8Encoding(false));
            writer.Write("original");
        }
        var expected = new ManagedModelArtifactFile { RelativePath = "source.zip", SizeBytes = new FileInfo(source).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))) };
        var target = Path.Combine(root, "bin", "tool.exe"); await File.WriteAllTextAsync(target, "original");
        await PinnedZipExpansionVerifier.VerifyAsync(source, expected, root, default);
        await File.WriteAllTextAsync(target, "modified and longer");
        await File.WriteAllTextAsync(Path.Combine(root, "extra.txt"), "unlisted");
        await PinnedZipExpansionVerifier.VerifyAsync(source, expected, root, default);
        using (ComponentAudit.ForceFull())
            await Assert.ThrowsAsync<InvalidDataException>(() => PinnedZipExpansionVerifier.VerifyAsync(source, expected, root, default));
        File.Delete(target);
        await Assert.ThrowsAsync<FileNotFoundException>(() => PinnedZipExpansionVerifier.VerifyAsync(source, expected, root, default));
    }

    private static PinnedTreeFile Entry(string path, string text) => new(path, Encoding.UTF8.GetByteCount(text),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
