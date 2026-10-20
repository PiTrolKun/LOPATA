using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services.Books;
using Lopata.Updates;

namespace AIHub.Tests;

[TestClass]
public sealed class BookOfficeBundleTests
{
    [TestMethod, DataRow("valid"), DataRow("corrupt"), DataRow("extra"), DataRow("duplicate"),
        DataRow("escape"), DataRow("alias"), DataRow("symlink"), DataRow("length"), DataRow("cancelled")]
    public async Task PrivateBundleIsExactPinnedAndPublishedOnlyAfterVerification(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "book-office-bundle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "office.zip"); var destination = Path.Combine(root, "cache");
        const string name = "program/python/lib/pip/_internal/models/candidate.py";
        var bytes = Encoding.UTF8.GetBytes("original pinned upstream code");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { Files = new[] { new { Name = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } } });
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var path = mode == "escape" ? "../escape.py" : mode == "alias" ? "program/../candidate.py" : name;
            var entry = zip.CreateEntry(path);
            if (mode == "symlink") entry.ExternalAttributes = 0xa000 << 16;
            using (var output = entry.Open()) output.Write(mode == "corrupt" ? new byte[bytes.Length] : mode == "length" ? [1] : bytes);
            if (mode is "extra" or "duplicate") using (zip.CreateEntry(mode == "extra" ? "extra.py" : name).Open()) { }
        }
        try
        {
            // Updater protections remain unchanged; the signed update carries only the outer ZIP.
            Assert.ThrowsExactly<InvalidDataException>(() => SafeUpdatePath.ValidateRelative("BookOfficeRuntime/" + name));
            using var token = new CancellationTokenSource(); if (mode == "cancelled") token.Cancel();
            using var input = new MemoryStream(manifest);
            if (mode == "valid")
            {
                await BookOfficeBundle.EnsureAsync(archive, destination, input, token.Token);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(destination, name)));
                using var second = new MemoryStream(manifest);
                await BookOfficeBundle.EnsureAsync(archive, destination, second, token.Token);
            }
            else
            {
                if (mode == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(() => BookOfficeBundle.EnsureAsync(archive, destination, input, token.Token));
                else await Assert.ThrowsAsync<InvalidDataException>(() => BookOfficeBundle.EnsureAsync(archive, destination, input, token.Token));
                Assert.IsFalse(Directory.Exists(destination));
                Assert.IsFalse(File.Exists(Path.Combine(root, "escape.py")));
                Assert.AreEqual(0, Directory.GetDirectories(root, "*.preparing-*").Length);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConcurrentMaterializationReusesVerifiedCacheAndNeverReplacesChangedExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "book-office-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "office.zip"); var destination = Path.Combine(root, "cache"); byte[] bytes = [1, 2, 3];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { Files = new[] { new { Name = "program/test.bin", Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } } });
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) using (var entry = zip.CreateEntry("program/test.bin").Open()) entry.Write(bytes);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            { using var input = new MemoryStream(manifest); await BookOfficeBundle.EnsureAsync(archive, destination, input, CancellationToken.None); }));
            var path = Path.Combine(destination, "program", "test.bin"); File.WriteAllBytes(path, [4, 5, 6]);
            using var input = new MemoryStream(manifest);
            await Assert.ThrowsAsync<InvalidDataException>(() => BookOfficeBundle.EnsureAsync(archive, destination, input, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
