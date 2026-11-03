using System.IO;
using System.Net.Http;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutChatAcquisitionTests
{
    [TestMethod]
    [DataRow("missing", 4, 0L)]
    [DataRow("partial", 4, 7L)]
    [DataRow("invalid", 4, 0L)]
    [DataRow("missing", 1, -1L)]
    public async Task CoreRouteUsesCurrentStorageAndOnlyResumesPartialBytes(string mode, int connections, long expectedRange)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            using var audit = ComponentAudit.UseTestStore(Path.Combine(root, "audit"), false);
            var models = Path.Combine(root, "models"); Directory.CreateDirectory(models);
            var settings = new StorageSettings { Models = new() { Locations = [new() { Path = models }] } };
            var state = await LetItOutModel.CheckAsync(settings, CancellationToken.None);
            Assert.IsNotNull(state.ModelPath);
            var original = new byte[] { 9, 8, 7, 6, 5, 4, 3 };
            Directory.CreateDirectory(Path.GetDirectoryName(state.ModelPath)!);
            if (mode == "partial")
            {
                await File.WriteAllBytesAsync(state.ModelPath + ".part", original);
                state.Availability = CoreModelAvailability.Partial;
            }
            else if (mode == "invalid")
            {
                await File.WriteAllBytesAsync(state.ModelPath, original);
                state.Availability = CoreModelAvailability.Invalid;
            }
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var previous = ManagedModelCatalog.CreateLetItOut(Path.Combine(root, "previous-models"));
            store.Upsert(previous);
            using var cancel = new CancellationTokenSource();
            var handler = new CancelAtFirstRequest(cancel);
            using var client = new HttpClient(handler);
            var stages = new List<string>();

            // A fake transport cancels before returning a response body: no real model or network is involved.
            await Assert.ThrowsAsync<OperationCanceledException>(() => LetItOutChatPreparation.DownloadCoreAsync(
                state, connections, new ProgressCapture(stages), cancel.Token, store, client));

            Assert.AreEqual(1, handler.Calls);
            Assert.AreEqual(expectedRange, handler.RangeStart ?? -1);
            var current = store.Load(ManagedModelCatalog.LetItOutArtifactId)!;
            Assert.AreEqual(Path.GetDirectoryName(state.ModelPath), current.InstallDirectory);
            Assert.AreEqual(ManagedModelCatalog.LetItOutArtifactId, current.ModelArtifactId);
            Assert.AreEqual(ManagedModelCatalog.CreateLetItOut(models).Files[0].SourceUrl, handler.Source);
            Assert.AreEqual(ManagedModelStatuses.Paused, current.Status);
            Assert.IsTrue(stages.Contains("DownloadingCore"));
            Assert.IsFalse(Directory.Exists(previous.InstallDirectory));
            if (mode == "partial") CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(state.ModelPath + ".part"));
            if (mode == "invalid")
            {
                CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(state.ModelPath));
                Assert.IsFalse(File.Exists(state.ModelPath + ".part"), "An invalid final GGUF must not become a resume prefix.");
            }
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    [TestMethod]
    public async Task MismatchedCorePathIsRejectedBeforeLibraryMutationOrNetwork()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var models = Path.Combine(root, "models");
            var old = ManagedModelCatalog.CreateLetItOut(Path.Combine(root, "previous-models")); store.Upsert(old);
            using var cancel = new CancellationTokenSource();
            var handler = new CancelAtFirstRequest(cancel); using var client = new HttpClient(handler);
            var state = new CoreModelCheckResult { ModelsRoot = models, ModelPath = Path.Combine(models, "other.gguf") };

            await Assert.ThrowsAsync<InvalidOperationException>(() => LetItOutChatPreparation.DownloadCoreAsync(
                state, 4, new ProgressCapture([]), cancel.Token, store, client));

            Assert.AreEqual(0, handler.Calls);
            Assert.AreEqual(old.InstallDirectory, store.Load(ManagedModelCatalog.LetItOutArtifactId)!.InstallDirectory);
        }
        finally { ManagedModelLibraryTests.DeleteRoot(root); }
    }

    private sealed class CancelAtFirstRequest(CancellationTokenSource cancel) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal long? RangeStart { get; private set; }
        internal string? Source { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; RangeStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            Source = request.RequestUri?.AbsoluteUri;
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        }
    }

    private sealed class ProgressCapture(List<string> stages) : IProgress<LetItOutChatPreparationProgress>
    { public void Report(LetItOutChatPreparationProgress progress) => stages.Add(progress.StageKey); }
}
