using System.Security.Cryptography;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ModelStorageRelocationTests
{
    [TestMethod]
    public void ScenariosWithoutTransferableBundlesStillAppearUnderTheirDirections()
    {
        using var fixture = new Fixture();
        var scenarios = ModelStorageCatalog.Create(new StorageSettings(), fixture.Store);

        CollectionAssert.AreEquivalent(ScenarioNavigationCatalog.Nodes
            .Where(node => node.Kind == ScenarioNavigationKind.Scenario && node.IsAvailable).Select(node => node.Id).ToArray(),
            scenarios.Where(scenario => scenario.Id != ManagedModelCatalog.CoreArtifactId).Select(scenario => scenario.Id).ToArray());
        var editor = scenarios.Single(scenario => scenario.Id == ScenarioNavigationCatalog.OfficeEditor);
        Assert.AreEqual(ScenarioNavigationCatalog.Utilities, editor.DirectionId);
        Assert.IsEmpty(editor.Groups);
    }

    [TestMethod]
    public async Task MiniMaxImageMovesFromSimpleGenerationWhileVideoKeepsItsOwnEncoderCopy()
    {
        using var fixture = new Fixture();
        var miniMax = ImageGenerationCatalog.Get(MiniMaxImageRuntime.ModelId);
        var artifact = ImageGenerationCatalog.Manifest.Artifacts.Single(item =>
            miniMax.Components.Contains(item.Id) && !ImageGenerationCatalog.IsRuntimeArtifact(item.Id));
        var image = fixture.Card(artifact.Id);
        image.Revision = artifact.Revision;
        image.Files[0].Purpose = "llm";
        fixture.Store.Upsert(image);

        var video = fixture.Card("video-h3-models");
        video.Revision = Services.VideoPreparation.VideoH3Catalog.Parts.Single(item => item.Id == video.ModelArtifactId).Revision;
        var videoEncoder = Path.Combine(video.InstallDirectory, video.Files[0].RelativePath);
        File.Copy(Path.Combine(image.InstallDirectory, image.Files[0].RelativePath), videoEncoder, overwrite: true);
        video.Files[0].Sha256 = image.Files[0].Sha256;
        video.Files[0].SizeBytes = image.Files[0].SizeBytes;
        fixture.Store.Upsert(video);

        var settings = new StorageSettings { Models = new() { Locations = [new() { Path = image.ModelsRoot }] } };
        ModelStorageGroup[] Groups(string scenarioId) => ModelStorageCatalog.Create(settings, fixture.Store)
            .Single(scenario => scenario.Id == scenarioId).Groups;
        var images = Groups(ScenarioNavigationCatalog.ImageGeneration).Single(group => group.Id == miniMax.Id);
        Assert.IsFalse(Groups(ScenarioNavigationCatalog.VideoGeneration).Any(group => group.Id == miniMax.Id));
        Assert.AreEqual(videoEncoder, images.VideoEncoderCopy?.Path);
        Assert.AreEqual(image.Files[0].SizeBytes, images.VideoEncoderCopy?.Bytes);

        var transfer = new ModelStorageTransfer(fixture.Store.Addresses, CopyFiles);
        var plan = transfer.Plan(images.Artifacts, fixture.Destination);
        var moved = plan.Items.Single();
        Assert.AreEqual(image.ModelArtifactId, moved.Address.ArtifactId);
        await transfer.MoveAsync(plan, 0, () => { }, CancellationToken.None);

        var relocated = Groups(ScenarioNavigationCatalog.ImageGeneration).Single(group => group.Id == miniMax.Id);
        Assert.AreEqual(moved.Address.Directory, relocated.Artifacts.Single().InstallDirectory);
        Assert.IsFalse(Groups(ScenarioNavigationCatalog.VideoGeneration).Any(group => group.Id == miniMax.Id));
        Assert.IsTrue(File.Exists(videoEncoder));
        Assert.AreEqual(videoEncoder, relocated.VideoEncoderCopy?.Path);
    }

    [TestMethod]
    public async Task PublishesWholeBundleAndUsesTheNewPathsDuringSubsequentRegistration()
    {
        using var fixture = new Fixture();
        var first = fixture.Card("test-image-weights");
        var second = fixture.Card("test-image-encoder");
        var unrelated = Path.Combine(first.InstallDirectory, "notes.txt");
        File.WriteAllText(unrelated, "user file");
        var oldFile = Path.Combine(first.InstallDirectory, first.Files[0].RelativePath);
        var transfer = new ModelStorageTransfer(fixture.Store.Addresses, async (items, owner, token) =>
        {
            await CopyFiles(items, owner, token);
            Assert.HasCount(0, fixture.Store.Addresses.Read());
            Assert.IsTrue(File.Exists(oldFile));
        });
        var plan = transfer.Plan([first, second, first], fixture.Destination);
        Assert.HasCount(2, plan.Items);
        var result = await transfer.MoveAsync(plan, 0, () => { }, CancellationToken.None);
        Assert.IsFalse(result.CleanupPending);
        Assert.AreEqual(plan.Bytes, result.ReleasedBytes);
        Assert.IsFalse(File.Exists(oldFile));
        Assert.IsTrue(File.Exists(unrelated));

        var reopened = new ManagedModelLibraryStore(fixture.Library);
        foreach (var item in plan.Items)
        {
            var registered = reopened.Load(item.Address.ArtifactId)!;
            Assert.AreEqual(item.Address.Directory, registered.InstallDirectory);
            var oldDefinition = new ManagedModelArtifactCard
            {
                ModelArtifactId = registered.ModelArtifactId, Revision = registered.Revision,
                InstallDirectory = item.Source, ModelsRoot = item.SourceRoot, Files = registered.Files
            };
            Assert.AreEqual(item.Address.Directory, reopened.Upsert(oldDefinition).InstallDirectory);
        }
        Assert.AreEqual(Path.Combine(plan.Items[0].Address.Directory, first.Files[0].RelativePath), reopened.Addresses.ResolvePreviousPath(oldFile));
    }

    [TestMethod]
    public async Task CancellationAndCorruptionLeaveEveryBundleOnTheOriginalDrive()
    {
        using var fixture = new Fixture();
        var first = fixture.Card("test-one");
        var second = fixture.Card("test-two");
        var cancel = new ModelStorageTransfer(fixture.Store.Addresses, async (items, owner, token) =>
        {
            await CopyFiles(items.Take(1).ToArray(), owner, token);
            throw new OperationCanceledException();
        });
        var plan = cancel.Plan([first, second], fixture.Destination);
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancel.MoveAsync(plan, 0, () => { }, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Store.Addresses.JournalPath));
        var corrupt = new ModelStorageTransfer(fixture.Store.Addresses, async (items, owner, token) =>
        {
            await CopyFiles(items, owner, token);
            var last = items[^1];
            var path = Path.Combine(last.DestinationFolder, Path.GetFileName(last.Source));
            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 1;
            File.WriteAllBytes(path, bytes);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupt.MoveAsync(plan, 0, () => { }, CancellationToken.None));
        Assert.HasCount(0, fixture.Store.Addresses.Read());
        Assert.IsTrue(plan.Items.All(item => File.Exists(Path.Combine(item.Source, item.Files[0].RelativePath))));
        Assert.IsTrue(plan.Items.All(item => !Directory.Exists(item.Address.Directory)));
        Assert.IsFalse(File.Exists(fixture.Store.Addresses.JournalPath));
    }

    [TestMethod]
    public void RecoveryPreservesTheOriginalWhenCommittedTargetHasSameSizeCorruption()
    {
        using var fixture = new Fixture();
        var card = fixture.Card("test-recovery");
        var transfer = new ModelStorageTransfer(fixture.Store.Addresses, CopyFiles);
        var plan = transfer.Plan([card], fixture.Destination);
        var item = plan.Items.Single();
        var source = Path.Combine(item.Source, item.Files[0].RelativePath);
        var target = Path.Combine(item.Address.Directory, item.Files[0].RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, new byte[item.Files[0].Bytes]);
        ModelStorageAddresses.WriteDocument(fixture.Store.Addresses.JournalPath, plan);
        fixture.Store.Addresses.Publish([item.Address]);

        Assert.IsTrue(transfer.TryRecover().CleanupPending);
        Assert.IsTrue(File.Exists(source));
        File.Copy(source, target, overwrite: true);
        Assert.IsFalse(transfer.TryRecover().CleanupPending);
        Assert.IsFalse(File.Exists(source));
        Assert.IsTrue(File.Exists(target));
    }

    [TestMethod]
    public async Task ModelOpenedByAnotherProcessPreventsCopying()
    {
        using var fixture = new Fixture();
        var card = fixture.Card("test-in-use");
        var transfer = new ModelStorageTransfer(fixture.Store.Addresses, (_, _, _) =>
            throw new AssertFailedException("Copy started while the original was in use."));
        var plan = transfer.Plan([card], fixture.Destination);
        var path = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
        using var openModel = File.OpenRead(path);

        var failure = await Assert.ThrowsAsync<IOException>(() => transfer.MoveAsync(plan, 0, () => { }, CancellationToken.None));
        Assert.AreEqual("ModelStorage.Busy", failure.Message);
        Assert.IsFalse(File.Exists(fixture.Store.Addresses.JournalPath));
    }

    [TestMethod]
    public async Task BackgroundWorkAndTransfersAreMutuallyExclusive()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Library, "work.lock");
        var first = new ModelStorageWorkGate(path);
        var second = new ModelStorageWorkGate(path);
        using (await first.EnterWorkAsync(CancellationToken.None))
        using (await second.EnterWorkAsync(CancellationToken.None))
            Assert.IsNull(second.TryBeginTransfer());
        var transfer = first.TryBeginTransfer();
        Assert.IsNotNull(transfer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var work = second.EnterWorkAsync(timeout.Token);
        Assert.IsFalse(work.IsCompleted);
        Assert.IsNull(first.TryBeginTransfer());
        transfer.Dispose();
        using var active = await work;
        Assert.IsNull(first.TryBeginTransfer());
    }

    [TestMethod]
    public async Task NativeWindowsCopyTransfersAndVerifiesFiles()
    {
        using var fixture = new Fixture(Environment.GetEnvironmentVariable("LOPATA_TRANSFER_TEST_SOURCE"),
            Environment.GetEnvironmentVariable("LOPATA_TRANSFER_TEST_TARGET"));
        var card = fixture.Card("test-native-copy");
        var transfer = new ModelStorageTransfer(fixture.Store.Addresses);
        var plan = transfer.Plan([card], fixture.Destination);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await transfer.MoveAsync(plan, 0, () => { }, deadline.Token);
        Assert.IsFalse(result.CleanupPending);
        Assert.AreEqual(plan.Bytes, result.ReleasedBytes);
        var moved = plan.Items.Single();
        Assert.IsTrue(File.Exists(Path.Combine(moved.Address.Directory, moved.Files.Single().RelativePath)));
    }

    private static Task CopyFiles(IReadOnlyList<ModelStorageCopyItem> items, nint owner, CancellationToken token)
    {
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(item.DestinationFolder);
            File.Copy(item.Source, Path.Combine(item.DestinationFolder, Path.GetFileName(item.Source)));
        }
        return Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _source;
        internal string Library { get; }
        internal string Destination { get; }
        internal ManagedModelLibraryStore Store { get; }

        internal Fixture(string? sourceParent = null, string? destinationParent = null)
        {
            var id = "lopata-model-transfer-test-" + Guid.NewGuid().ToString("N");
            _root = Path.GetFullPath(Path.Combine(sourceParent ?? Path.GetTempPath(), id));
            _source = Path.Combine(_root, "models");
            Library = Path.Combine(_root, "library");
            Destination = destinationParent is null ? Path.Combine(_root, "destination") : Path.GetFullPath(Path.Combine(destinationParent, id));
            Directory.CreateDirectory(_source);
            Directory.CreateDirectory(Destination);
            Store = new(Library);
        }

        internal ManagedModelArtifactCard Card(string id)
        {
            var content = System.Text.Encoding.UTF8.GetBytes(id + " weights");
            var directory = Path.Combine(_source, "ImageGeneration", id);
            var relative = Path.Combine("model", "weights.gguf");
            var file = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
            return Store.Upsert(new ManagedModelArtifactCard
            {
                ModelArtifactId = id, DisplayName = id, Revision = "revision-1", ModelsRoot = _source, InstallDirectory = directory,
                IsManaged = true, Status = ManagedModelStatuses.Installed,
                Files = [new() { RelativePath = relative, SizeBytes = content.Length, Sha256 = Convert.ToHexString(SHA256.HashData(content)) }]
            });
        }

        public void Dispose()
        {
            foreach (var directory in new[] { Destination, _root })
            {
                if (!Directory.Exists(directory)) continue;
                var name = Path.GetFileName(directory);
                if (name != "destination" && !name.StartsWith("lopata-model-transfer-test-", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected relocation fixture path.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
