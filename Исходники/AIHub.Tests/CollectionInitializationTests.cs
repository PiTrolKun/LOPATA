using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Lopata.Collection;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class CollectionInitializationTests
{
    [TestMethod]
    public void ATestOrToolHostCannotInitializeProductionRegistryAndSettings()
        => Assert.Throws<InvalidOperationException>(() => CollectionInitializationService.InitializeCurrentAsync().GetAwaiter().GetResult());

    [TestMethod]
    public async Task FreshDefaultInitializesNodeBeforeMainWindowWithPersistedAuditOff()
    {
        using var f = new Fixture();
        f.Settings.Behavior.ComponentAuditEnabled = false;
        await f.Initialize();
        Assert.IsTrue(f.Registration.IsRegistered);
        var config = CollectionConfig.Load(f.Paths.CollectionDirectory);
        Assert.IsTrue(config.Enabled); Assert.IsFalse(config.AuditEnabled); Assert.IsFalse(ComponentAudit.Enabled);
        var actualWorker = config.ExpectedWorkerPath;
        Assert.AreEqual(f.Worker, actualWorker);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(f.Root, "audit", "receipts"), "*.json").Length);
        var revision = config.Revision;
        await f.Initialize();
        Assert.AreEqual(revision, CollectionConfig.Load(f.Paths.CollectionDirectory).Revision);
    }

    [TestMethod]
    public async Task ExplicitOffDoesNotRequirePayloadOrCreateCollectionState()
    {
        using var f = new Fixture(); f.Settings.Behavior.CollectionIntegrationEnabled = false;
        File.Delete(f.Native); File.Delete(f.Worker);
        await f.Initialize();
        Assert.IsFalse(f.Registration.HasOwnedRegistration);
        Assert.IsFalse(Directory.Exists(f.Paths.CollectionDirectory));
    }

    [TestMethod]
    public async Task ExplicitOffDisablesOwnedOrphanEvenWithMalformedConfiguration()
    {
        using var f = new Fixture();
        f.Registration.Enable(f.Native, f.Worker, f.Paths.CollectionDirectory, "ru");
        Directory.CreateDirectory(f.Paths.CollectionDirectory);
        File.WriteAllText(Path.Combine(f.Paths.CollectionDirectory, "config.json"), "{broken");
        f.Settings.Behavior.CollectionIntegrationEnabled = false;
        await f.Initialize();
        Assert.IsFalse(f.Registration.HasOwnedRegistration);
        Assert.AreEqual(0U, BitConverter.ToUInt32(File.ReadAllBytes(Path.Combine(f.Paths.CollectionDirectory, "enabled.bin"))));
        Assert.AreEqual("{broken", File.ReadAllText(Path.Combine(f.Paths.CollectionDirectory, "config.json")));
    }

    [TestMethod]
    public async Task AnotherInstallationCannotChangeCollectionConfigurationOrAuditIt()
    {
        using var f = new Fixture(); await f.Initialize();
        var configPath = Path.Combine(f.Paths.CollectionDirectory, "config.json");
        var before = File.ReadAllBytes(configPath);
        var another = new CollectionShellRegistration(Path.Combine(f.Paths.ApplicationDirectory, "another.exe"), f.RegistryRoot);
        await Assert.ThrowsAsync<IOException>(() => CollectionInitializationService.InitializeAsync(f.Settings, f.Storage, f.Paths, another));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(configPath));
        Assert.IsTrue(f.Registration.IsRegistered);
    }

    [TestMethod]
    public async Task FirstUseAlwaysAuditsPayloadBeforePublishingConfiguration()
    {
        using var f = new Fixture(); f.Settings.Behavior.ComponentAuditEnabled = false;
        File.WriteAllText(f.Native, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Initialize());
        Assert.IsFalse(f.Registration.HasOwnedRegistration);
        Assert.IsFalse(File.Exists(Path.Combine(f.Paths.CollectionDirectory, "config.json")));
    }

    [TestMethod]
    public async Task RegistryFailureRestoresPreviousWorkerAndEnabledState()
    {
        using var f = new Fixture();
        new CollectionConfig { Enabled = false, ExpectedWorkerPath = f.Worker, Roots = [f.Root] }.Save(f.Paths.CollectionDirectory);
        var previous = CollectionConfig.Load(f.Paths.CollectionDirectory);
        var failing = new CollectionShellRegistration(Path.Combine(f.Paths.ApplicationDirectory, "AIHub.exe"), f.RegistryRoot,
            step => { if (step == "namespace") throw new IOException("Injected namespace failure."); });
        await Assert.ThrowsAsync<IOException>(() => CollectionInitializationService.InitializeAsync(f.Settings, f.Storage, f.Paths, failing));
        var restored = CollectionConfig.Load(f.Paths.CollectionDirectory);
        Assert.IsFalse(restored.Enabled); Assert.AreEqual(previous.ExpectedWorkerPath, restored.ExpectedWorkerPath);
        CollectionAssert.AreEqual(previous.Roots, restored.Roots); Assert.IsFalse(f.Registration.HasOwnedRegistration);
    }

    [TestMethod]
    public void SharedConfigurationPreservesAllKnownOutputsAndPrivateExclusions()
    {
        using var f = new Fixture();
        f.Settings.MusicOutputFolder = Path.Combine(f.Root, "music");
        f.Settings.ImageGeneration.Folder = Path.Combine(f.Root, "images");
        f.Settings.ScreenCapture.Folder = Path.Combine(f.Root, "captures");
        f.Settings.Behavior.CollectionFolders = [Path.Combine(f.Root, "manual")];
        f.Storage.Results.Locations = [new() { Path = Path.Combine(f.Root, "results") }];
        f.Storage.Models.Locations = [new() { Path = Path.Combine(f.Root, "models") }];
        var config = CollectionConfigurationFactory.Create(f.Settings, f.Storage, f.Paths, true, f.Worker);
        foreach (var name in new[] { "music", "images", "captures", "manual", "results" })
            Assert.Contains(Path.Combine(f.Root, name), config.Roots);
        foreach (var name in new[] { "OfficeEditor/Drafts", "OfficeReader/Sessions", "OfficeReader/Speech/Temporary",
            "OfficeReader/Translation/Temporary", "VideoPreparation/Recovery", "VideoEditor/Projects", "VectorGraphics/EditorRecovery",
            "VectorGraphics/Jobs", "VideoPreparation/H3Jobs", "VideoPreparation/VaceJobs", "Chat/Audio",
            "SpeechAssets/Sessions", "VoiceNotifications", "Localization/VoiceNotifications" })
            Assert.Contains(Path.GetFullPath(Path.Combine(f.Paths.DataDirectory, name)), config.ExcludedRoots);
        Assert.Contains(f.Paths.ApplicationDirectory, config.ExcludedRoots);
        Assert.Contains(f.Paths.RuntimeDirectory, config.ExcludedRoots);
        Assert.Contains(f.Paths.CollectionDirectory, config.ExcludedRoots);
        Assert.Contains(Path.Combine(f.Root, "models"), config.ExcludedRoots);
        Assert.Contains(Path.Combine(f.Paths.ProjectRoot!, "_tmp"), config.ExcludedRoots);
        Assert.Contains(Path.Combine(f.Paths.ProjectRoot!, "_backups"), config.ExcludedRoots);
        Assert.Contains(Path.Combine(f.Paths.ProjectRoot!, "Исходники", "AIHub", "bin"), config.ExcludedRoots);
        Assert.Contains(Path.Combine(f.Paths.ProjectRoot!, "Исходники", "AIHub", "Assets", "VoiceNotifications"), config.ExcludedRoots);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LOPATA-CollectionInitialization-tests", Guid.NewGuid().ToString("N"));
        internal string RegistryRoot { get; } = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        internal CollectionConfigurationPaths Paths { get; }
        internal string Native { get; }
        internal string Worker { get; }
        internal AppSettings Settings { get; } = new();
        internal StorageSettings Storage { get; } = new();
        internal CollectionShellRegistration Registration { get; }
        private readonly bool _auditEnabled = ComponentAudit.Enabled;
        private readonly IDisposable _audit;
        internal Fixture()
        {
            Paths = new(Path.Combine(Root, "app"), Path.Combine(Root, "data"), Path.Combine(Root, "runtime"), Path.Combine(Root, "project"));
            var generation = new string('a', 24); var directory = Path.Combine(Paths.ApplicationDirectory, "Collection", generation);
            Directory.CreateDirectory(directory);
            Native = Path.Combine(directory, "LOPATA.CollectionShell.dll"); Worker = Path.Combine(directory, "LOPATA.CollectionWorker.exe");
            File.WriteAllText(Native, "native fixture"); File.WriteAllText(Worker, "worker fixture");
            var files = new[] { Native, Worker }.Select(path => new CollectionPayloadFile(generation + "/" + Path.GetFileName(path),
                new FileInfo(path).Length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
            var rid = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "win-arm64" : "win-x64";
            var source = new string('b', 64);
            var lines = new[] { "collection-payload-v2|" + rid + "|" + source }.Concat(files.OrderBy(p => p.Path, StringComparer.Ordinal)
                .Select(p => p.Path + "|" + p.Size + "|" + p.Sha256));
            var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
            File.WriteAllText(Path.Combine(Paths.ApplicationDirectory, "Collection", "current.json"),
                JsonSerializer.Serialize(new CollectionPayload(1, revision, files[0].Path, files[1].Path, files, source, rid), CollectionFiles.Json));
            Registration = new(Path.Combine(Paths.ApplicationDirectory, "AIHub.exe"), RegistryRoot);
            _audit = ComponentAudit.UseTestStore(Path.Combine(Root, "audit"));
        }
        internal Task Initialize() => CollectionInitializationService.InitializeAsync(Settings, Storage, Paths, Registration);
        public void Dispose()
        {
            _audit.Dispose(); ComponentAudit.Configure(_auditEnabled);
            Registry.CurrentUser.DeleteSubKeyTree(RegistryRoot, false);
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-CollectionInitialization-tests")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(prefix, Path.GetFullPath(Root)); Directory.Delete(Root, true);
        }
    }
}
