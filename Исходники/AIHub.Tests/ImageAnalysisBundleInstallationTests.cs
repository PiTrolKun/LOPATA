using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageAnalysisBundleInstallationTests
{
    [TestMethod]
    public void MediumKimiCard_UsesPinnedChatLlmArtifactWithoutASeparateProjector()
    {
        var card = ManagedModelCatalog.CreateKimiMedium(@"C:\models");

        Assert.AreEqual("GGMM", card.Format);
        Assert.AreEqual("Q4_1", card.Quantization);
        Assert.AreEqual(ChatLlmBackendPaths.DisplayName, card.RuntimeBackend);
        Assert.AreEqual(1, card.Files.Count);
        Assert.AreEqual("main_model", card.Files[0].Purpose);
        Assert.AreEqual(10_447_149_104, card.Files[0].SizeBytes);
        Assert.AreEqual(
            "33700ea2f4c8467fbcc4efa060c763e035a8e73003424634125b5a3c64ce02c9",
            card.Files[0].Sha256);
    }

    [TestMethod]
    public void Check_WithEmptyConfiguredStorage_RequiresOnlyMissingArtifacts()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var settings = CreateSettings(modelsRoot);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            using var service = new ImageAnalysisBundleInstallationService(store);

            var snapshot = service.Check(settings);

            Assert.AreEqual(ImageAnalysisBundleInstallStates.DownloadRequired, snapshot.State);
            Assert.HasCount(1, snapshot.Components);
            Assert.AreEqual(ManagedModelCatalog.OmniBetaArtifactId, snapshot.Components[0].ModelArtifactId);
            Assert.IsTrue(snapshot.MissingBytes > 0);
            Assert.IsFalse(snapshot.CanStart);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void HeavyCheck_RequiresOnlyPinnedOmniAndReportsSourceAndDisk()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            using var service = new ImageAnalysisBundleInstallationService(store);

            var snapshot = service.Check(
                CreateSettings(modelsRoot),
                ImageAnalysisBundleCatalog.HeavyId);

            Assert.AreEqual(ImageAnalysisBundleInstallStates.DownloadRequired, snapshot.State);
            Assert.HasCount(1, snapshot.Components);
            Assert.AreEqual(ManagedModelCatalog.OmniGammaArtifactId, snapshot.Components[0].ModelArtifactId);
            Assert.AreEqual(ManagedModelCatalog.OmniGammaRepository, snapshot.Components[0].RepositoryId);
            Assert.AreEqual(ManagedModelCatalog.OmniGammaRevision, snapshot.Components[0].Revision);
            Assert.AreEqual("Apache-2.0", snapshot.Components[0].License);
            Assert.IsTrue(snapshot.AvailableFreeBytes > 0);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Inventory_RegistersUnknownGgufAsReadOnlyExternalModel()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var externalPath = Path.Combine(modelsRoot, "spontaneous-model.gguf");
            File.WriteAllBytes(externalPath, [0x47, 0x47, 0x55, 0x46]);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));

            var cards = new ManagedModelInventoryService(store).Synchronize(CreateSettings(modelsRoot));
            var external = cards.Single(card => card.DisplayName == "spontaneous-model.gguf");

            Assert.IsFalse(external.IsManaged);
            Assert.IsFalse(external.CanRemoveFiles);
            Assert.IsFalse(external.SupportsDirectDownload);
            Assert.AreEqual(ManagedModelStatuses.External, external.Status);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void RemovingKimiFiles_DoesNotTouchCoreOrFlorence()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var kimi = CreateTinyCard(modelsRoot, ManagedModelCatalog.KimiMediumArtifactId, "Vision", "kimi.gguf");
            var core = CreateTinyCard(modelsRoot, ManagedModelCatalog.CoreArtifactId, "Core", "core.gguf");
            var florence = CreateTinyCard(modelsRoot, ManagedModelCatalog.FlorenceLargeArtifactId, "Shared", "model.safetensors");
            foreach (var card in new[] { kimi, core, florence })
            {
                Directory.CreateDirectory(card.InstallDirectory);
                File.WriteAllBytes(Path.Combine(card.InstallDirectory, card.Files[0].RelativePath), [1]);
                store.Upsert(card);
            }

            new ManagedModelRemovalService(store).RemoveFiles(kimi.ModelArtifactId, true);

            Assert.IsFalse(File.Exists(Path.Combine(kimi.InstallDirectory, "kimi.gguf")));
            Assert.IsTrue(File.Exists(Path.Combine(core.InstallDirectory, "core.gguf")));
            Assert.IsTrue(File.Exists(Path.Combine(florence.InstallDirectory, "model.safetensors")));
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Inventory_PreservesFilesRemovedStatusForPredefinedArtifact()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelCatalog.CreateKimiMedium(modelsRoot);
            card.Status = ManagedModelStatuses.FilesRemoved;
            card.StoredBytes = 0;
            store.Upsert(card);

            var cards = new ManagedModelInventoryService(store).Synchronize(CreateSettings(modelsRoot));
            var reloaded = cards.Single(item => item.ModelArtifactId == ManagedModelCatalog.KimiMediumArtifactId);

            Assert.AreEqual(ManagedModelStatuses.FilesRemoved, reloaded.Status);
            Assert.AreEqual(0, reloaded.StoredBytes);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Inventory_PreservesSourceUnavailableStatusAndDiagnostic()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelCatalog.CreateFlorenceLarge(modelsRoot);
            card.Status = ManagedModelStatuses.SourceUnavailable;
            card.LastError = "HTTP 404";
            store.Upsert(card);

            var cards = new ManagedModelInventoryService(store).Synchronize(CreateSettings(modelsRoot));
            var reloaded = cards.Single(item => item.ModelArtifactId == ManagedModelCatalog.FlorenceLargeArtifactId);

            Assert.AreEqual(ManagedModelStatuses.SourceUnavailable, reloaded.Status);
            Assert.AreEqual("HTTP 404", reloaded.LastError);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Check_TreatsPreservedFilesAfterNetworkFailureAsResumable()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelCatalog.CreateOmniBeta(modelsRoot);
            var firstFile = card.Files[0];
            Directory.CreateDirectory(card.InstallDirectory);
            File.WriteAllBytes(
                Path.Combine(card.InstallDirectory, firstFile.RelativePath + ".part"),
                new byte[128]);
            card.Status = ManagedModelStatuses.SourceUnavailable;
            card.LastError = "Temporary DNS failure";
            card.StoredBytes = 128;
            store.Upsert(card);
            using var service = new ImageAnalysisBundleInstallationService(store);

            var snapshot = service.Check(CreateSettings(modelsRoot));
            var reloaded = snapshot.Components.Single(item =>
                item.ModelArtifactId == ManagedModelCatalog.OmniBetaArtifactId);

            Assert.AreEqual(ImageAnalysisBundleInstallStates.ResumeAvailable, snapshot.State);
            Assert.AreEqual(ManagedModelStatuses.Paused, reloaded.Status);
            Assert.AreEqual("Temporary DNS failure", reloaded.LastError);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void FlorenceErrorExtraction_PrefersMarkedCauseOverPythonWarnings()
    {
        var standardError = "warning one\r\nwarning two\r\n"
            + ImageAnalysisRuntimeCompatibilityService.FlorenceErrorMarker
            + "Required torchvision is missing.";

        var result = ImageAnalysisRuntimeCompatibilityService.ExtractFlorenceError(standardError);

        Assert.AreEqual("Required torchvision is missing.", result);
    }

    [TestMethod]
    public void FlorenceErrorExtraction_UsesLastNonEmptyLineWithoutMarker()
    {
        var result = ImageAnalysisRuntimeCompatibilityService.ExtractFlorenceError(
            "first warning\r\n\r\nactual failure\r\n");

        Assert.AreEqual("actual failure", result);
    }

    private static ManagedModelArtifactCard CreateTinyCard(
        string modelsRoot,
        string id,
        string folder,
        string fileName) => new()
        {
            ModelArtifactId = id,
            DisplayName = id,
            RepositoryId = "unit/" + id,
            Revision = "revision",
            IsManaged = true,
            CanRemoveFiles = true,
            ModelsRoot = modelsRoot,
            InstallDirectory = Path.Combine(modelsRoot, folder, id),
            Status = ManagedModelStatuses.Installed,
            StoredBytes = 1,
            Files =
            [
                new ManagedModelArtifactFile
                {
                    RelativePath = fileName,
                    SizeBytes = 1,
                    Sha256 = new string('0', 64)
                }
            ]
        };

    private static StorageSettings CreateSettings(string modelsRoot) => new()
    {
        Models = new StorageCategorySettings
        {
            Locations =
            [
                new StorageLocationSettings { Path = modelsRoot }
            ]
        }
    };
}
