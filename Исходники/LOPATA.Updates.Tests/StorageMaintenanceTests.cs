using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class StorageMaintenanceTests
{
    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task CleanupPreservesThreeUsableRollbackPoints(int target)
    {
        using var fixture = new UpdateFixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() };
        var engine = new UpdateTransaction(fixture.Roots, keys, Path.Combine(fixture.Folder, "state"));
        var previous = Release(1);
        fixture.Put("AIHub.exe", "version-1");
        fixture.Put("user.txt", "keep");
        await engine.RegisterInstalledAsync(previous);
        var ids = new List<string>();
        for (var number = 2; number <= 7; number++)
        {
            var next = Release(number);
            var staging = Path.Combine(fixture.Stage, "app");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "AIHub.exe"), "version-" + number);
            var id = await engine.PrepareAsync(previous, next, fixture.Stage);
            await engine.ApplyAsync(id);
            await engine.ConfirmHealthyAsync(id);
            ids.Add(id);
            previous = next;
        }
        Assert.IsEmpty(engine.PlanStorageCleanup(fixture.Stage).Groups);
        CollectionAssert.AreEqual(new[] { "0.9.6-beta", "0.9.5-beta", "0.9.4-beta" }, engine.RollbackVersions().ToArray());
        var proposal = engine.PlanStorageCleanup(fixture.Stage, 10);
        CollectionAssert.AreEqual(ids.Take(2).ToArray(), proposal.Groups.Select(x => x.Id).ToArray());
        Assert.AreEqual(proposal.Reclaimable, await engine.CleanStorageAsync(fixture.Stage, proposal));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.App, "user.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.App, ".lopata-update", ids[0], "backup", "AIHub.exe")));
        await engine.RollbackToAsync($"0.9.{target}-beta");
        Assert.AreEqual("version-" + target, File.ReadAllText(Path.Combine(fixture.App, "AIHub.exe")));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(fixture.App, "user.txt")));

        SignedManifest Release(int version) => SignedManifest.Sign(UpdateFixture.Manifest($"0.9.{version}-beta",
            UpdateFixture.Entry("AIHub.exe", "version-" + version)), "test", key);
    }

    [TestMethod]
    public void UpdateWorkspaceUsesTheConfiguredResultsDrive()
    {
        using var fixture = new UpdateFixture();
        var profile = Path.Combine(fixture.Folder, "profile");
        Directory.CreateDirectory(profile);
        var legacy = Path.Combine(profile, "state", "installed");
        var configured = Path.Combine(fixture.Folder, "результаты");
        Directory.CreateDirectory(configured);
        Assert.AreEqual(legacy, ConfiguredUpdateWorkspace.Resolve(profile, legacy));
        File.WriteAllText(Path.Combine(profile, "storage-settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { results = new { locations = new[] { new { path = configured } } } }));
        Assert.AreEqual(Path.Combine(configured, "AI_HUB", "Temporary", "Updates", "installed"),
            ConfiguredUpdateWorkspace.Resolve(profile, legacy));
    }
}
