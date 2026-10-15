using System.Text.Json;
using System.Net.Http;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class RagBookPreparationTests
{
    [TestMethod]
    public async Task ActualBookDefinitionsRegisterAndVerifyThroughSharedLibrary()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            using var preparation = new RagBookModelPreparation(Path.Combine(root, "models"), 3, store);
            foreach (var id in new[] { RagBookModels.EmbeddingId, RagBookModels.AssistantId })
            {
                var card = await preparation.PrepareAsync(id, false, new Progress<ManagedModelDownloadProgress>(), default);
                Assert.AreEqual(id, store.Load(id)?.ModelArtifactId);
                Assert.AreEqual(ManagedModelStatuses.FilesRemoved, card.Status);
                Assert.IsNull(card.LastVerifiedAt);
            }
            Assert.HasCount(2, store.LoadAll());
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "models")), "Verification must not start a download.");
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("model..escape")]
    [DataRow(".model")]
    [DataRow("model.")]
    [DataRow("../model")]
    [DataRow("model/escape")]
    [DataRow("model\\escape")]
    [DataRow("model:stream")]
    [DataRow("model\uFF0Eescape")]
    public void NamespacedArtifactIdsStillRejectPathsAndEmptySegments(string id)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            Assert.Throws<InvalidDataException>(() => store.Load(id));
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task LivePinnedSmallFilesDownloadAndVerifyForBothBookModels()
    {
        if (Environment.GetEnvironmentVariable("LOPATA_RAG_LIVE_DOWNLOAD") != "1")
            Assert.Inconclusive("Opt in to a bounded real network check with LOPATA_RAG_LIVE_DOWNLOAD=1.");
        var root = ManagedModelLibraryTests.CreateRoot();
        var previous = ComponentLicenseGate.ConfirmAsync;
        try
        {
            var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(root, "receipts.json"));
            ComponentLicenseGate.ConfirmAsync = (ids, token) => licenses.EnsureAsync(ids, _ => Task.FromResult(true), token);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            using var downloads = new ManagedModelAcquisitionService(store, client) { MaximumParallelConnections = 3 };
            foreach (var card in RagBookModels.Cards(Path.Combine(root, "models")))
            {
                // Use exact pinned metadata, but only small supporting files in an isolated library.
                // This receipt must never be mistaken for a complete production model installation.
                card.Files = card.Files.Where(f => f.SizeBytes < 100_000).ToList();
                Assert.IsTrue(card.Files.Any(f => f.RelativePath == "README.md"));
                Assert.IsTrue(card.TotalBytes < 1_000_000);
                store.Upsert(card);
                var downloaded = await downloads.DownloadAsync(card.ModelArtifactId, null, default);
                Assert.IsNotNull(downloaded.LastVerifiedAt);
                Assert.AreEqual(downloaded.TotalBytes, downloaded.StoredBytes);
                Assert.IsTrue(RagBookModelPreparation.FilesPresent(downloaded));
                Console.WriteLine($"Live pinned files: {card.ModelArtifactId}; {card.Files.Count} files; {card.TotalBytes} bytes; SHA256 verified.");
                var file = downloaded.Files[0];
                await File.AppendAllTextAsync(Path.Combine(card.InstallDirectory, file.RelativePath), "corrupt");
                var corrupted = await downloads.VerifyAsync(card.ModelArtifactId, null, default);
                Assert.AreEqual(ManagedModelStatuses.Corrupted, corrupted.Status);
            }
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public void PinnedModelsHaveSeparateIndexesAndRegisteredLicenseTexts()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rag-manifest-" + Guid.NewGuid().ToString("N"));
        var cards = RagBookModels.Cards(root);
        var licenses = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(root, "receipts.json"));
        Assert.AreEqual(2, cards.Count);
        foreach (var card in cards)
        {
            var license = licenses.Entries.Single(l => l.Id == card.ModelArtifactId);
            Assert.IsFalse(license.Basic);
            Assert.AreEqual(card.Revision, license.Version);
            Assert.IsTrue(license.Texts.Length >= 2);
            foreach (var file in license.Texts) Assert.IsTrue(licenses.ReadText(file).Length > 50);
            Assert.IsTrue(card.TotalBytes > 1_000_000_000);
            Assert.IsFalse(card.InstallDirectory.Contains("480M", StringComparison.Ordinal));
            Assert.IsTrue(card.Files.Any(f => f.RelativePath.EndsWith("README.md", StringComparison.Ordinal)));
        }
        Assert.IsFalse(Directory.Exists(root), "Inspecting definitions must not acquire files.");
    }

    [TestMethod]
    public async Task DeclinedLicensePreventsRegistrationAndDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rag-gate-" + Guid.NewGuid().ToString("N"));
        var previous = ComponentLicenseGate.ConfirmAsync;
        try
        {
            var requested = new List<string>();
            ComponentLicenseGate.ConfirmAsync = (ids, _) => { requested.AddRange(ids); throw new OperationCanceledException(); };
            using var preparation = new RagBookModelPreparation(Path.Combine(root, "models"), 3, new ManagedModelLibraryStore(Path.Combine(root, "library")));
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparation.PrepareAsync(RagBookModels.AssistantId,
                true, new Progress<ManagedModelDownloadProgress>(), default));
            CollectionAssert.AreEqual(new[] { RagBookModels.AssistantId }, requested);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DraftSurvivesReopenAndDoesNotModifySources()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-rag-draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); var book = Path.Combine(root, "книга.txt"); File.WriteAllText(book, "Рифма и размер.");
            var draft = new RagBookDraft { Name = "Стихосложение", OutputDirectory = Path.Combine(root, "output"), Books = [book] };
            var path = Path.Combine(root, "workspace.json"); new RagBookDraftStore(path).Save(draft);
            var restored = new RagBookDraftStore(path).Load();
            Assert.AreEqual(draft.Name, restored.Name); CollectionAssert.AreEqual(draft.Books, restored.Books);
            Assert.AreEqual("Рифма и размер.", File.ReadAllText(book)); Assert.IsFalse(Directory.Exists(draft.OutputDirectory));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
            File.WriteAllText(path, "corrupt data");
            Assert.Throws<IOException>(() => new RagBookDraftStore(path).Load());
            Assert.AreEqual("corrupt data", File.ReadAllText(path));
            File.WriteAllText(path, "{\"Books\":null}");
            Assert.Throws<IOException>(() => new RagBookDraftStore(path).Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
