using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class PackageBuilderTests
{
    [TestMethod]
    public async Task IdenticalPackagesKeepTheirOriginalUrlAndNotesSurviveSkippedReleases()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "binary"); fixture.Put("Localization/ru.json", "strings");
        var roots = new Dictionary<string, string> { ["app"] = fixture.App };
        var note = new UpdateNote("0.2.42-beta", DateTimeOffset.UtcNow, "First release");
        var first = await UpdatePackageBuilder.BuildAsync(roots, Path.Combine(fixture.Folder, "first"),
            "0.2.42-beta", new('a', 40), [note]);
        var second = await UpdatePackageBuilder.BuildAsync(roots, Path.Combine(fixture.Folder, "second"),
            "0.2.43-beta", new('b', 40), [note, new("0.2.43-beta", DateTimeOffset.UtcNow, "Next release")], first);
        CollectionAssert.AreEqual(first.Packages, second.Packages, "Byte-identical ZIPs must reuse published package URLs.");
        Assert.AreEqual(2, second.Notes.Length);
        var plan = await UpdatePlanner.CreateAsync(first, second, fixture.Roots);
        Assert.AreEqual(0, plan.DownloadBytes);
        foreach (var package in first.Packages)
            await UpdatePackageExtractor.ExtractAsync(Path.Combine(fixture.Folder, "first", package.Id), package, first, fixture.Stage);
        Assert.AreEqual("strings", File.ReadAllText(Path.Combine(fixture.Stage, "app", "Localization", "ru.json")));
    }

    [TestMethod]
    public async Task OutputCannotContaminateThePayloadItIsPackaging()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "binary");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageBuilder.BuildAsync(
            new Dictionary<string, string> { ["app"] = fixture.App }, Path.Combine(fixture.App, "output"),
            "0.2.42-beta", new('a', 40), [new("0.2.42-beta", DateTimeOffset.UtcNow, "notes")]));
    }
}
