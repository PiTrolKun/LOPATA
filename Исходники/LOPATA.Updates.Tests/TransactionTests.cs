using System.Security.Cryptography;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class TransactionTests
{
    [TestMethod]
    public async Task ApplyAndHealthKeepNewVersionWithRecoverableBackup()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        await engine.ApplyAsync(id);
        Assert.AreEqual(UpdatePhase.AwaitingHealth, engine.ReadJournal()!.Phase);
        fixture.AssertNew();
        await engine.ConfirmHealthyAsync(id);
        await engine.RecoverAsync();
        fixture.AssertNew();
        Assert.AreEqual(UpdatePhase.Healthy, engine.ReadJournal()!.Phase);
        Assert.IsTrue(Directory.Exists(Path.Combine(fixture.Files.App, ".lopata-update", id, "backup")));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task InterruptedReplacementRestoresWholeOldInstallation(int stopAfter)
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        await Assert.ThrowsAsync<SimulatedInterruption>(() => engine.ApplyAsync(id, p =>
        {
            if (p.CompletedFiles == stopAfter) throw new SimulatedInterruption();
        }));
        var restarted = fixture.Engine();
        await restarted.RecoverAsync();
        fixture.AssertOld();
        Assert.AreEqual(UpdatePhase.RolledBack, restarted.ReadJournal()!.Phase);
        await restarted.RecoverAsync();
        fixture.AssertOld();
    }

    [TestMethod]
    public async Task UnconfirmedLaunchAndInterruptedRecoveryRemainRecoverable()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        await engine.ApplyAsync(id);
        await Assert.ThrowsAsync<SimulatedInterruption>(() => fixture.Engine().RecoverAsync(p =>
        {
            if (p.CompletedFiles == 1) throw new SimulatedInterruption();
        }));
        await fixture.Engine().RecoverAsync();
        fixture.AssertOld();
    }

    [TestMethod]
    public async Task ExternalEditAfterPreparationStopsBeforeAnyLiveMutation()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        fixture.Files.Put("AIHub.exe", "external");
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(id));
        Assert.AreEqual(UpdatePhase.Prepared, engine.ReadJournal()!.Phase);
        await engine.RecoverAsync();
        Assert.AreEqual("external", File.ReadAllText(Path.Combine(fixture.Files.App, "AIHub.exe")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Files.App, "new.dll")));
    }

    [TestMethod]
    public async Task SecondUpdaterCannotAcquireInstallationLock()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        using var locked = new FileStream(Path.Combine(fixture.State, "update.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(id));
        fixture.AssertOld();
    }

    [TestMethod]
    public async Task SharedRootLockAlsoProtectsAgainstDifferentStateDirectories()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        using var locked = new FileStream(Path.Combine(fixture.Files.App, ".lopata-update", "update.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(id));
        fixture.AssertOld();
    }

    [TestMethod]
    public async Task LockedExecutableDoesNotPreventLaterRecovery()
    {
        using var fixture = new TransactionFixture();
        var (engine, id) = await fixture.PrepareAsync();
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing semantics required.");
        using (var locked = new FileStream(Path.Combine(fixture.Files.App, "AIHub.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => engine.ApplyAsync(id));
        await fixture.Engine().RecoverAsync();
        fixture.AssertOld();
    }

    private sealed class SimulatedInterruption : Exception;

    private sealed class TransactionFixture : IDisposable
    {
        public UpdateFixture Files { get; } = new();
        public string State => Path.Combine(Files.Folder, "state");
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string> { ["fixture"] = _key.ExportSubjectPublicKeyInfoPem() };
        public UpdateTransaction Engine() => new(Files.Roots, Keys, State);

        public async Task<(UpdateTransaction Engine, string Id)> PrepareAsync()
        {
            Files.Put("AIHub.exe", "old"); Files.Put("old.dll", "obsolete"); Files.Put("book.txt", "user data");
            var previous = SignedManifest.Sign(UpdateFixture.Manifest("0.2.42-beta",
                UpdateFixture.Entry("AIHub.exe", "old"), UpdateFixture.Entry("old.dll", "obsolete")), "fixture", _key);
            var (path, manifest) = Files.Zip(new() { ["app/AIHub.exe"] = "new", ["app/new.dll"] = "added" },
                UpdateFixture.Manifest("0.2.43-beta", UpdateFixture.Entry("AIHub.exe", "new"), UpdateFixture.Entry("new.dll", "added")));
            var target = SignedManifest.Sign(manifest, "fixture", _key);
            await UpdatePackageExtractor.ExtractAsync(path, manifest.Packages[0], manifest, Files.Stage);
            var engine = Engine();
            await engine.RegisterInstalledAsync(previous);
            return (engine, await engine.PrepareAsync(previous, target, Files.Stage));
        }

        public void AssertOld()
        {
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(Files.App, "AIHub.exe")));
            Assert.AreEqual("obsolete", File.ReadAllText(Path.Combine(Files.App, "old.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(Files.App, "new.dll")));
            Assert.AreEqual("user data", File.ReadAllText(Path.Combine(Files.App, "book.txt")));
        }

        public void AssertNew()
        {
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(Files.App, "AIHub.exe")));
            Assert.AreEqual("added", File.ReadAllText(Path.Combine(Files.App, "new.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(Files.App, "old.dll")));
            Assert.AreEqual("user data", File.ReadAllText(Path.Combine(Files.App, "book.txt")));
        }

        public void Dispose() { _key.Dispose(); Files.Dispose(); }
    }
}
