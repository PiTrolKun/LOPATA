using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class PlannerAndPackageTests
{
    [TestMethod]
    public async Task SkippedReleasesNeedOnlyChangedPackagesAndRemoveOnlyOwnedFiles()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "old"); fixture.Put("library.dll", "same");
        fixture.Put("obsolete.dll", "old"); fixture.Put("my-book.txt", "user manuscript");
        var previous = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "old"),
            UpdateFixture.Entry("library.dll", "same"), UpdateFixture.Entry("obsolete.dll", "old"));
        var target = UpdateFixture.Manifest("0.2.49-beta", UpdateFixture.Entry("AIHub.exe", "new"),
            UpdateFixture.Entry("library.dll", "same", "unchanged.zip"));
        var plan = await UpdatePlanner.CreateAsync(previous, target, fixture.Roots);
        Assert.AreEqual(2, plan.ChangedFiles);
        Assert.AreEqual("files.zip", plan.Packages.Single().Id);
        Assert.AreEqual("obsolete.dll", plan.Operations.Single(p => p.Action == UpdateAction.Delete).File.Path);
        Assert.AreEqual("user manuscript", File.ReadAllText(Path.Combine(fixture.App, "my-book.txt")));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")), "Planning must not modify installation.");
    }

    [TestMethod]
    public async Task UnownedAndLocallyModifiedFilesAreNeverOverwrittenOrDeleted()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("local.txt", "user");
        var target = UpdateFixture.Manifest("0.2.43-beta", UpdateFixture.Entry("local.txt", "replacement"));
        await Assert.ThrowsAsync<IOException>(() => UpdatePlanner.CreateAsync(null, target, fixture.Roots));
        var previous = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("local.txt", "original"));
        await Assert.ThrowsAsync<IOException>(() => UpdatePlanner.CreateAsync(previous, target, fixture.Roots));
        target = UpdateFixture.Manifest("0.2.43-beta", UpdateFixture.Entry("new.dll", "new"));
        await Assert.ThrowsAsync<IOException>(() => UpdatePlanner.CreateAsync(previous, target, fixture.Roots));
        Assert.AreEqual("user", File.ReadAllText(Path.Combine(fixture.App, "local.txt")));
    }

    [TestMethod]
    public async Task MatchingInstallationNeedsNoDownloadAndDamagedMissingFileCanBeRestored()
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "same");
        var manifest = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "same"));
        var plan = await UpdatePlanner.CreateAsync(manifest, manifest, fixture.Roots);
        Assert.AreEqual(0, plan.DownloadBytes);
        File.Delete(Path.Combine(fixture.App, "AIHub.exe"));
        plan = await UpdatePlanner.CreateAsync(manifest, manifest, fixture.Roots);
        Assert.AreEqual(UpdateAction.Write, plan.Operations.Single().Action);
    }

    [TestMethod]
    public async Task CancellationAndDowngradeDoNotChangeInstallation()
    {
        using var fixture = new UpdateFixture();
        var previous = UpdateFixture.Manifest("0.2.49-beta", UpdateFixture.Entry("AIHub.exe", "old"));
        var older = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "new"));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePlanner.CreateAsync(previous, older, fixture.Roots));
        await Assert.ThrowsAsync<OperationCanceledException>(() => UpdatePlanner.CreateAsync(null, previous, fixture.Roots, new(true)));
    }

    [TestMethod]
    public async Task PackageExtractsOnlyValidatedFilesIntoStaging()
    {
        using var fixture = new UpdateFixture();
        var (path, manifest) = fixture.Zip(new() { ["app/AIHub.exe"] = "payload" },
            UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "payload")));
        await UpdatePackageExtractor.ExtractAsync(path, manifest.Packages[0], manifest, fixture.Stage);
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(fixture.Stage, "app", "AIHub.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.App, "AIHub.exe")));
    }

    [TestMethod]
    public async Task WrongHashTraversalAndUnexpectedZipEntryCannotBeInstalled()
    {
        using var fixture = new UpdateFixture();
        var manifest = UpdateFixture.Manifest("0.2.42-beta", UpdateFixture.Entry("AIHub.exe", "payload"));
        foreach (var name in new[] { "../outside.txt", "app/../outside.txt", "app/unknown.dll" })
        {
            var bad = fixture.Zip(new() { [name] = "payload" }, manifest);
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageExtractor.ExtractAsync(
                bad.Path, bad.Manifest.Packages[0], bad.Manifest, fixture.Stage));
        }
        var corrupt = fixture.Zip(new() { ["app/AIHub.exe"] = "PAYLOAD" }, manifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackageExtractor.ExtractAsync(
            corrupt.Path, corrupt.Manifest.Packages[0], corrupt.Manifest, fixture.Stage));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Stage, "app", "AIHub.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Folder, "outside.txt")));
    }
}
