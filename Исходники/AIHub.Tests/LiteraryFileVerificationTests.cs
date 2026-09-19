using System.Security.Cryptography;
using System.Text;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryFileVerificationTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIHub-verification-" + Guid.NewGuid().ToString("N"));
    private string Model => Path.Combine(_root, "model.bin");
    private string Cache => Path.Combine(_root, "cache");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed class Progress(Action<double> action) : IProgress<double> { public void Report(double value) => action(value); }
    [TestInitialize] public void Initialize() { Directory.CreateDirectory(_root); File.WriteAllText(Model, "test"); }
    [TestCleanup] public void Cleanup()
    {
        if (Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "AIHub-verification-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }

    [TestMethod] public async Task SuccessfulDigestPersistsAndAvoidsAnotherRead()
    {
        var full = 0; var progress = new Progress(p => { if (p == 0) full++; });
        Assert.IsTrue(await new LiteraryFileVerification(Cache).ValidAsync(Model, 4, Hash("test"), "sha256", default, progress: progress));
        Assert.IsTrue(await new LiteraryFileVerification(Cache).ValidAsync(Model, 4, Hash("test"), "sha256", default, progress: progress));
        Assert.AreEqual(1, full);
    }

    [TestMethod] public async Task ChangedFileAndExpectedDigestInvalidateReceipt()
    {
        var check = new LiteraryFileVerification(Cache);
        Assert.IsTrue(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("else"), "sha256", default));
        Assert.IsTrue(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        File.WriteAllText(Model, "else"); File.SetLastWriteTimeUtc(Model, DateTime.UtcNow.AddSeconds(3));
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        Assert.IsFalse(await new LiteraryFileVerification(Cache).ValidAsync(Model, 4, Hash("test"), "sha256", default));
    }

    [TestMethod] public async Task ForceDetectsDamageEvenWithPreservedMetadataAndRemovesOldSuccess()
    {
        var check = new LiteraryFileVerification(Cache);
        Assert.IsTrue(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        var written = File.GetLastWriteTimeUtc(Model);
        File.WriteAllText(Model, "else"); File.SetLastWriteTimeUtc(Model, written);
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default, force: true));
        Assert.IsFalse(await new LiteraryFileVerification(Cache).ValidAsync(Model, 4, Hash("test"), "sha256", default));
    }

    [TestMethod] public async Task MissingResizedAndMalformedReceiptsNeverReportReady()
    {
        var check = new LiteraryFileVerification(Cache);
        Assert.IsTrue(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        File.WriteAllText(Directory.GetFiles(Cache).Single(), "broken json");
        File.WriteAllText(Model, "else");
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        File.WriteAllText(Model, "too long");
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        File.Delete(Model);
        Assert.IsFalse(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
    }

    [TestMethod] public async Task CancelledRecheckLeavesNoReceiptAndConcurrentChecksShareSuccess()
    {
        var check = new LiteraryFileVerification(Cache); var full = 0;
        var progress = new Progress(p => { if (p == 0) full++; });
        var checks = Enumerable.Range(0, 4).Select(_ => check.ValidAsync(Model, 4, Hash("test"), "sha256", default, progress: progress));
        Assert.IsTrue((await Task.WhenAll(checks)).All(x => x)); Assert.AreEqual(1, full);
        using var cancel = new CancellationTokenSource();
        try
        {
            await check.ValidAsync(Model, 4, Hash("test"), "sha256", cancel.Token, true, new Progress(_ => cancel.Cancel()));
            Assert.Fail("Cancellation must propagate");
        }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, Directory.GetFiles(Cache).Length);
    }

    [TestMethod] public async Task GitBlobDigestAndChangedAlgorithmAreVerified()
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("blob 4\0test")));
        var check = new LiteraryFileVerification(Cache);
        Assert.IsTrue(await check.ValidAsync(Model, 4, hash, "gitsha1", default));
        Assert.IsFalse(await check.ValidAsync(Model, 4, hash, "sha256", default));
        Assert.IsTrue(await check.ValidAsync(Model, 4, Hash("test"), "sha256", default));
        var other = Path.Combine(_root, "other.bin"); File.WriteAllText(other, "else");
        Assert.IsFalse(await check.ValidAsync(other, 4, Hash("test"), "sha256", default));
    }
}
