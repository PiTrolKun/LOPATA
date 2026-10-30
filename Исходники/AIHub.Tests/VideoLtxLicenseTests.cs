using System.Security.Cryptography;
using System.Text;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoLtxLicenseTests
{
    private static ComponentLicenseService Catalog(string state) =>
        new(Path.Combine(AppContext.BaseDirectory, "Licenses"), state);

    [TestMethod]
    public void OptionalLtxTermsIncludeOriginalAgreementAndDoNotGrantUnreviewedRights()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var service = Catalog(files.File("receipts.json"));
        var entry = service.Entries.Single(e => e.Id == "video-ltx25-models");
        Assert.IsFalse(entry.Basic);
        Assert.AreEqual("download", entry.Delivery);
        Assert.AreEqual("49b093a39c19169a8a81721dcc541e07e945232f", entry.Version);
        Assert.IsFalse(ComponentLicenseService.ExpandSelection(["basic"], service.Entries).Contains(entry.Id));
        Assert.IsTrue(entry.Ru.Contains("права публичной поставки ещё не подтверждены", StringComparison.Ordinal));
        Assert.IsTrue(entry.En.Contains("rights for public delivery have not been established", StringComparison.Ordinal));
        var original = string.Concat(entry.Texts.Select(service.ReadText));
        Assert.IsTrue(original.Contains("License date: August 11, 2026", StringComparison.Ordinal));
        Assert.IsTrue(original.Contains("20) To use LTX-2.x", StringComparison.Ordinal));
        Assert.IsTrue(original.Contains("directly competes", StringComparison.Ordinal));
        var terms = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.License + entry.Ru + entry.En + original)));
        Assert.AreEqual(terms, entry.Terms);
    }

    [TestMethod]
    public async Task DecliningLtxAcknowledgementCreatesNoReceipt()
    {
        using var files = new VideoPreparationTests.TestFiles();
        var state = files.File("receipts.json");
        var service = Catalog(state);
        var confirmations = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.EnsureAsync(["video-ltx25-models"], pending =>
        {
            confirmations++;
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual("video-ltx25-models", pending[0].Id);
            return Task.FromResult(false);
        }, default));
        Assert.AreEqual(1, confirmations);
        Assert.IsFalse(File.Exists(state));
    }
}
