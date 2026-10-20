using System.IO;
using System.Net.Http;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Books.Smart;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class BookSmartPreparationServiceTests
{
    [TestMethod]
    public void OvisRuntimeIncludesThePinnedPillowNeededForIllustrationCropsWithoutPaddle()
    {
        var artifacts = BookSmartRuntimeManifest.Artifacts(new("ovis"));
        Assert.AreEqual(2, artifacts.Count);
        Assert.IsTrue(artifacts.Any(artifact => artifact.Name == "python"));
        Assert.IsTrue(artifacts.Any(artifact => artifact.Name == "pillow" && artifact.Version == "12.1.1"));
        Assert.IsFalse(artifacts.Any(artifact => artifact.Name is "paddlepaddle" or "paddleocr"));
    }

    [TestMethod]
    public void OcrManifestsUseImmutableCompletePinsAndThePaddleLayoutDependency()
    {
        var paddle = BookSmartOcrCatalog.CreateCards(new(), "C:\\fixture-models");
        var ovis = BookSmartOcrCatalog.CreateCards(new("ovis"), "C:\\fixture-models");
        Assert.AreEqual(2, paddle.Count); Assert.AreEqual(1, ovis.Count);
        Assert.AreEqual(5, paddle.Sum(card => card.Files.Count)); Assert.AreEqual(2, ovis[0].Files.Count);
        foreach (var card in paddle.Concat(ovis))
        {
            Assert.AreEqual(40, card.Revision.Length);
            foreach (var file in card.Files)
            { Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(file.SizeBytes > 0); Assert.IsTrue(file.SourceUrl.Contains(card.Revision, StringComparison.Ordinal)); }
        }
        Assert.AreEqual(ovis[0].Revision, BookSmartOcrCatalog.Profile(new("ovis")).Revision);
        Assert.AreEqual(BookSmartOcrCatalog.OvisId, BookSmartOcrCatalog.Profile(new("ovis")).ArtifactId);
    }

    [TestMethod, DataRow(false, "alpha"), DataRow(true, "alpha"), DataRow(true, "beta")]
    public void CardPlanContainsOnlySelectedDependenciesAndReusesTheCoreDirectory(bool images, string vision)
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-book-smart-plan-" + Guid.NewGuid().ToString("N"));
        using var service = new BookSmartPreparationService(root, images, key => key, CreateOcr);
        var cards = service.CreateCards(new("ovis", vision));
        Assert.AreEqual(images ? 3 : 2, cards.Count);
        Assert.AreEqual(Path.Combine(root, "Core", "Qwen3-8B"), cards.Single(card => card.ModelArtifactId == ManagedModelCatalog.CoreArtifactId).InstallDirectory);
        Assert.IsTrue(cards.Any(card => card.ModelArtifactId == "fixture-ovis"));
        Assert.IsFalse(cards.Any(card => card.ModelArtifactId == "fixture-paddle"));
        if (images) Assert.IsTrue(cards.Any(card => card.ModelArtifactId == (vision == "alpha" ? ManagedModelCatalog.OmniAlphaArtifactId : ManagedModelCatalog.OmniBetaArtifactId)));
        else Assert.IsFalse(cards.Any(card => card.ModelArtifactId is ManagedModelCatalog.OmniAlphaArtifactId or ManagedModelCatalog.OmniBetaArtifactId));
    }

    [TestMethod]
    public async Task LocalCheckDoesNotDownloadAndCannotApproveMissingModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-book-smart-check-" + Guid.NewGuid().ToString("N"));
        var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
        var handler = new NoNetwork(); using var http = new HttpClient(handler);
        using var acquisition = new ManagedModelAcquisitionService(store, http);
        using var service = new BookSmartPreparationService(Path.Combine(root, "models"), false, key => key, CreateOcr,
            (_, _, _, _) => Task.FromResult<BookSmartPreparationItem[]>([new("runtime fixture", "ready", 0, true)]), store, acquisition);
        try
        {
            var readiness = await service.PrepareAsync(new(), false, new SilentProgress(), CancellationToken.None);
            Assert.IsFalse(readiness.Ready); Assert.AreEqual(3, readiness.Items.Length);
            Assert.IsTrue(readiness.Items[0].MissingBytes > 0); Assert.AreEqual(3, readiness.Items[1].MissingBytes);
            Assert.AreEqual(0, handler.Calls);
            CollectionAssert.AreEquivalent(new[] { ManagedModelCatalog.CoreArtifactId, "fixture-paddle" }, store.LoadAll().Select(card => card.ModelArtifactId).ToArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IReadOnlyList<ManagedModelArtifactCard> CreateOcr(BookSmartSelection selection, string root) =>
    [new() { ModelArtifactId = "fixture-" + selection.OcrId, DisplayName = selection.OcrId,
        RepositoryId = "fixture/ocr", Revision = new string('a', 40), IsManaged = true, ModelsRoot = root,
        InstallDirectory = Path.Combine(root, "ocr", selection.OcrId),
        Files = [new() { RelativePath = "ocr.bin", SizeBytes = 3, Sha256 = new string('b', 64), SourceUrl = "https://example.invalid/ocr.bin" }] }];
    private sealed class SilentProgress : IProgress<string> { public void Report(string value) { } }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("A local readiness check must not make an HTTP request."); }
    }
}
