using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Lopata.Updates;
using Lopata.UpdateTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class ReleaseInputPreparationTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PreparationRetainsOldPathsAndExactTextWithoutUndoingRealChanges(bool withBom)
    {
        using var fixture = new UpdateFixture();
        fixture.Put("AIHub.exe", "previous application");
        fixture.Put("Collection/previous/module.dll", "signed old module");
        fixture.Put("Licenses/vendor.txt", "Copyright\r\nOriginal terms\r\n");
        fixture.Put("Tools/worker.py", "print('previous')\r\n");
        var oldPackages = Path.Combine(fixture.Folder, "previous-packages");
        var previous = await Build(fixture.App, oldPackages);
        var current = Path.Combine(fixture.Folder, "current"); Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "AIHub.exe"), "new application");
        Directory.CreateDirectory(Path.Combine(current, "Licenses")); Directory.CreateDirectory(Path.Combine(current, "Tools"));
        File.WriteAllText(Path.Combine(current, "Licenses/vendor.txt"), "Copyright\nOriginal terms\n", new UTF8Encoding(withBom));
        File.WriteAllText(Path.Combine(current, "Tools/worker.py"), "print('new implementation')\n");
        var roots = new Dictionary<string, string> { ["app"] = current };
        var result = await ReleaseInputPreparation.RestorePreviousAsync(roots, previous, oldPackages,
            Path.Combine(fixture.Folder, "preparation"));
        CollectionAssert.Contains(result.RestoredPaths, "app/Collection/previous/module.dll");
        CollectionAssert.Contains(result.RestoredTextBytes, "app/Licenses/vendor.txt");
        CollectionAssert.Contains(result.ContentChangesKept, "app/Tools/worker.py");
        CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(fixture.App, "Licenses/vendor.txt")),
            File.ReadAllBytes(Path.Combine(current, "Licenses/vendor.txt")));
        Assert.AreEqual("new application", File.ReadAllText(Path.Combine(current, "AIHub.exe")));
        Assert.AreEqual("print('new implementation')\n", File.ReadAllText(Path.Combine(current, "Tools/worker.py")));
        var next = await Build(current, Path.Combine(fixture.Folder, "next-packages"));
        ReleaseInputPreparation.EnsurePreviousPaths(next, previous);
        foreach (var package in next.Packages)
            await UpdatePackageExtractor.ExtractAsync(Path.Combine(fixture.Folder, "next-packages", package.Id), package, next, fixture.Stage);
        Assert.AreEqual("signed old module", File.ReadAllText(Path.Combine(fixture.Stage, "app/Collection/previous/module.dll")));
    }

    [TestMethod]
    public async Task SignedUpdateReplacesAnOldScriptWithTheBytesRequiredByTheApplication()
    {
        using var fixture = new UpdateFixture();
        var testAssembly = typeof(ReleaseInputPreparationTests).Assembly;
        using var resource = testAssembly.GetManifestResourceStream("Tests.H3Memory")!;
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var currentBytes = buffer.ToArray();
        var text = Encoding.UTF8.GetString(currentBytes);
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var oldText = lf == text ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
        const string script = "Tools/h3_memory.py";
        fixture.Put(script, oldText);
        File.Copy(testAssembly.Location, Path.Combine(fixture.App, "AIHub.dll"));
        var oldPackages = Path.Combine(fixture.Folder, "old-packages");
        var previous = await Build(fixture.App, oldPackages);

        var current = Path.Combine(fixture.Folder, "publish");
        Directory.CreateDirectory(Path.Combine(current, "Tools"));
        File.Copy(testAssembly.Location, Path.Combine(current, "AIHub.dll"));
        var project = Path.Combine(fixture.Folder, "AIHub.csproj");
        File.WriteAllText(project, """
            <Project><ItemGroup>
              <EmbeddedResource Include="Tools\h3_memory.py" LogicalName="Tests.H3Memory" />
            </ItemGroup></Project>
            """);
        var publishedScript = SafeUpdatePath.Resolve(current, script);
        File.WriteAllText(publishedScript, oldText);
        var wrong = await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseScriptContracts.VerifyPublishedAsync(current, project));
        StringAssert.Contains(wrong.Message, script);
        File.WriteAllBytes(publishedScript, currentBytes);

        var contracts = await ReleaseScriptContracts.VerifyPublishedAsync(current, project);
        var roots = new Dictionary<string, string> { ["app"] = current };
        var changes = await ReleaseInputPreparation.RestorePreviousAsync(roots, previous, oldPackages,
            Path.Combine(fixture.Folder, "prep"), publishedFiles: contracts.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        CollectionAssert.DoesNotContain(changes.RestoredTextBytes, "app/" + script);
        CollectionAssert.AreEqual(currentBytes, File.ReadAllBytes(publishedScript));

        var nextPackages = Path.Combine(fixture.Folder, "next-packages");
        const string version = "0.7.74-beta";
        var target = await UpdatePackageBuilder.BuildAsync(roots, nextPackages, version, new('a', 40),
            [new(version, DateTimeOffset.UtcNow, "Script repair test")], previous);
        Assert.Throws<InvalidDataException>(() => ReleaseScriptContracts.VerifyManifest(previous, contracts));
        ReleaseScriptContracts.VerifyManifest(target, contracts);
        var package = target.Packages.Single(p => p.Id == target.Files.Single(f => f.Path == script).Package);
        await UpdatePackageExtractor.ExtractAsync(Path.Combine(nextPackages, package.Id), package, target, fixture.Stage);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var before = SignedManifest.Sign(previous, "test", key);
        var transaction = new UpdateTransaction(fixture.Roots, keys, Path.Combine(fixture.Folder, "state"));
        await transaction.RegisterInstalledAsync(before);
        var id = await transaction.PrepareAsync(before, SignedManifest.Sign(target, "test", key), fixture.Stage);
        await transaction.ApplyAsync(id);
        await transaction.ConfirmHealthyAsync(id);
        CollectionAssert.AreEqual(currentBytes, File.ReadAllBytes(SafeUpdatePath.Resolve(fixture.App, script)));
        await ReleaseScriptContracts.VerifyPublishedAsync(fixture.App, project);
    }

    [TestMethod]
    public async Task CorruptPredecessorStopsBeforeAnyOldFileIsPublished()
    {
        using var fixture = new UpdateFixture(); fixture.Put("Collection/old.dll", "original module");
        var packages = Path.Combine(fixture.Folder, "packages"); var previous = await Build(fixture.App, packages);
        await File.AppendAllTextAsync(Path.Combine(packages, previous.Packages.Single().Id), "corrupt");
        var current = Path.Combine(fixture.Folder, "current"); Directory.CreateDirectory(current);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseInputPreparation.RestorePreviousAsync(
            new Dictionary<string, string> { ["app"] = current }, previous, packages, Path.Combine(fixture.Folder, "prep")));
        Assert.IsFalse(File.Exists(Path.Combine(current, "Collection/old.dll")));
    }

    [TestMethod]
    public async Task PreparationCannotContaminateThePublishRootAndMissingPathsCannotBeSignedSilently()
    {
        using var fixture = new UpdateFixture(); fixture.Put("old.dll", "old");
        var previous = await Build(fixture.App, Path.Combine(fixture.Folder, "packages"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseInputPreparation.RestorePreviousAsync(
            new Dictionary<string, string> { ["app"] = fixture.App }, previous, fixture.Folder, Path.Combine(fixture.App, "prep")));
        var changed = previous with { Files = [previous.Files.Single() with { Path = "different.dll" }] };
        Assert.Throws<InvalidDataException>(() => ReleaseInputPreparation.EnsurePreviousPaths(changed, previous));
    }

    [TestMethod]
    public async Task LinkedDeveloperSourceIsMaterializedButThePackerStillRejectsLinkedRoots()
    {
        using var fixture = new UpdateFixture(); var source = Path.Combine(fixture.Folder, "native"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "music-server.exe"), "signed");
        var legacy = UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry("studio/native/music-server.exe", "signed"));
        var link = Path.Combine(fixture.Folder, "linked-native");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("pwsh") { CreateNoWindow = true, UseShellExecute = false };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
                + "' -Target '" + source.Replace("'", "''") + "' | Out-Null");
            using var process = Process.Start(start)!; await process.WaitForExitAsync(); Assert.AreEqual(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, source);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Build(link, Path.Combine(fixture.Folder, "unsafe-packages")));
            var prepared = await ReleaseInputPreparation.MaterializeLegacyAsync(legacy, link, Path.Combine(fixture.Folder, "prepared"));
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(prepared) & FileAttributes.ReparsePoint);
            Assert.AreEqual("signed", File.ReadAllText(Path.Combine(prepared, "music-server.exe")));
            var manifest = await Build(prepared, Path.Combine(fixture.Folder, "safe-packages"));
            Assert.AreEqual(1, manifest.Files.Length);
        }
        finally { Directory.Delete(link); } // Remove the fixture junction itself before recursive fixture cleanup.
    }

    [TestMethod]
    public async Task SameSizeCorruptionOfLegacyInputIsRejectedBeforePackaging()
    {
        using var fixture = new UpdateFixture(); var source = Path.Combine(fixture.Folder, "native"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "music-server.exe"), "evil");
        var legacy = UpdateFixture.Manifest("0.7.65-beta", UpdateFixture.Entry("studio/native/music-server.exe", "good"));
        var stage = Path.Combine(fixture.Folder, "prepared");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseInputPreparation.MaterializeLegacyAsync(legacy, source, stage));
        Assert.IsFalse(Directory.EnumerateFiles(stage, "music-server.exe", SearchOption.AllDirectories).Any());
    }

    private static Task<UpdateManifest> Build(string source, string output) => UpdatePackageBuilder.BuildAsync(
        new Dictionary<string, string> { ["app"] = source }, output, "0.7.73-beta", new('a', 40),
        [new("0.7.73-beta", DateTimeOffset.UtcNow, "Release input preparation test")]);
}
