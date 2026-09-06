using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ManagedModelOperationsTests
{
    [TestMethod]
    public void Removal_DeletesOnlyManifestFilesAndPreservesCard()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var libraryRoot = Path.Combine(root, "library");
            var modelsRoot = Path.Combine(root, "models");
            var store = new ManagedModelLibraryStore(libraryRoot);
            var card = ManagedModelLibraryTests.CreateCard(modelsRoot);
            Directory.CreateDirectory(card.InstallDirectory);
            var managedPath = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
            var unrelatedPath = Path.Combine(card.InstallDirectory, "keep.txt");
            File.WriteAllBytes(managedPath, [1, 2, 3, 4]);
            File.WriteAllText(unrelatedPath, "keep");
            card.Status = ManagedModelStatuses.Installed;
            card.StoredBytes = 4;
            store.Upsert(card);

            var result = new ManagedModelRemovalService(store).RemoveFiles(card.ModelArtifactId, true);

            Assert.AreEqual(4, result.RemovedBytes);
            Assert.IsFalse(File.Exists(managedPath));
            Assert.IsTrue(File.Exists(unrelatedPath));
            var persisted = store.Load(card.ModelArtifactId);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(ManagedModelStatuses.FilesRemoved, persisted.Status);
            Assert.AreEqual("org/example", persisted.RepositoryId);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Removal_RejectsManifestPathOutsideInstallDirectory()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"));
            card.Files[0].RelativePath = "..\\outside.gguf";
            card.Status = ManagedModelStatuses.Installed;
            store.Upsert(card);

            Assert.Throws<InvalidDataException>(() =>
                new ManagedModelRemovalService(store).RemoveFiles(card.ModelArtifactId, true));
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public void Removal_BlocksPinnedAndActiveModels()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"));
            card.IsPinned = true;
            store.Upsert(card);
            Assert.Throws<InvalidOperationException>(() =>
                new ManagedModelRemovalService(store).RemoveFiles(card.ModelArtifactId, true));

            card.IsPinned = false;
            store.Upsert(card);
            var service = new ManagedModelRemovalService(store, new DelegateModelUsageGuard(_ => true));
            Assert.Throws<InvalidOperationException>(() => service.RemoveFiles(card.ModelArtifactId, true));
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task DownloadRemoveAndRedownload_UsesTheSamePinnedSource()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            byte[] payload = [1, 2, 3, 4];
            var handler = new RecordingPayloadHandler(payload);
            using var client = new HttpClient(handler);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"), payload);
            store.Upsert(card);
            using var acquisition = new ManagedModelAcquisitionService(store, client);

            await acquisition.DownloadAsync(card.ModelArtifactId, null, CancellationToken.None);
            new ManagedModelRemovalService(store).RemoveFiles(card.ModelArtifactId, true);
            await acquisition.DownloadAsync(card.ModelArtifactId, null, CancellationToken.None);

            Assert.AreEqual(2, handler.Requests.Count);
            Assert.IsTrue(handler.Requests.All(uri => uri == card.Files[0].SourceUrl));
            var persisted = store.Load(card.ModelArtifactId);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(ManagedModelStatuses.Installed, persisted.Status);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task DownloadFailure_PreservesCardAsSourceUnavailable()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            using var client = new HttpClient(new StatusHandler(HttpStatusCode.NotFound));
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"));
            store.Upsert(card);
            using var acquisition = new ManagedModelAcquisitionService(store, client);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                acquisition.DownloadAsync(card.ModelArtifactId, null, CancellationToken.None));

            var persisted = store.Load(card.ModelArtifactId);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(ManagedModelStatuses.SourceUnavailable, persisted.Status);
            Assert.AreEqual(card.Files[0].SourceUrl, persisted.Files[0].SourceUrl);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    [DataRow("core")]
    [DataRow("light")]
    [DataRow("medium")]
    public async Task ParallelDownload_ReusesContiguousPartialAndAssemblesExactPayload(string bundle)
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var payload = Enumerable.Range(0, 1024 * 1024)
                .Select(index => (byte)(index % 251))
                .ToArray();
            const int prefixLength = 128 * 1024;
            var handler = new RangePayloadHandler(payload, supportsRanges: true);
            using var client = new HttpClient(handler);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"), payload);
            if (bundle != "core")
            {
                card.ModelArtifactId = OmniLlamaProfile.ForBundle(bundle)!.ArtifactId;
                card.Role = ManagedModelRoles.Vision;
                var projector = (bundle == "light" ? ManagedModelCatalog.CreateOmniAlpha(card.ModelsRoot) : ManagedModelCatalog.CreateOmniBeta(card.ModelsRoot)).Files[1];
                projector.SizeBytes = payload.Length;
                projector.Sha256 = card.Files[0].Sha256;
                card.Files.Add(projector);
            }
            store.Upsert(card);
            var targetPath = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
            Directory.CreateDirectory(card.InstallDirectory);
            await File.WriteAllBytesAsync(targetPath + ".part", payload[..prefixLength]);
            using var acquisition = new ManagedModelAcquisitionService(store, client, 64 * 1024)
            {
                MaximumParallelConnections = 4
            };

            await acquisition.DownloadAsync(card.ModelArtifactId, null, CancellationToken.None);

            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(targetPath));
            Assert.IsTrue(handler.MaximumActiveRequests >= 2);
            Assert.IsTrue(handler.RequestedRanges.Count >= 5);
            if (bundle == "core") Assert.IsTrue(handler.RequestedRanges.All(range => range.From >= prefixLength));
            if (bundle != "core")
            {
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(card.InstallDirectory, card.Files[1].RelativePath)));
                Assert.AreEqual(ManagedModelStatuses.Installed, store.Load(card.ModelArtifactId)!.Status);
            }
            Assert.IsFalse(SegmentedModelFileDownloader.GetPartialArtifactPaths(targetPath).Any(File.Exists));
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ParallelDownload_FallsBackToSingleRequestWhenRangesAreUnsupported()
    {
        var root = ManagedModelLibraryTests.CreateRoot();
        try
        {
            var payload = Enumerable.Range(0, 512 * 1024)
                .Select(index => (byte)(index % 239))
                .ToArray();
            var handler = new RangePayloadHandler(payload, supportsRanges: false);
            using var client = new HttpClient(handler);
            var store = new ManagedModelLibraryStore(Path.Combine(root, "library"));
            var card = ManagedModelLibraryTests.CreateCard(Path.Combine(root, "models"), payload);
            store.Upsert(card);
            using var acquisition = new ManagedModelAcquisitionService(store, client, 64 * 1024)
            {
                MaximumParallelConnections = 8
            };

            await acquisition.DownloadAsync(card.ModelArtifactId, null, CancellationToken.None);

            var targetPath = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath);
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(targetPath));
            Assert.AreEqual(2, handler.RequestCount);
            Assert.AreEqual(1, handler.RequestedRanges.Count);
        }
        finally
        {
            ManagedModelLibraryTests.DeleteRoot(root);
        }
    }

    private sealed class RecordingPayloadHandler(byte[] payload) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RangePayloadHandler(byte[] payload, bool supportsRanges) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumActiveRequests;
        private int _requestCount;

        public int MaximumActiveRequests => _maximumActiveRequests;

        public int RequestCount => _requestCount;

        public List<(long From, long To)> RequestedRanges { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(20, cancellationToken);
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                if (range?.From is { } requestedFrom)
                {
                    lock (RequestedRanges)
                    {
                        RequestedRanges.Add((requestedFrom, range.To ?? payload.LongLength - 1));
                    }
                }
                if (!supportsRanges || range?.From is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(payload)
                    };
                }
                var from = range.From.Value;
                var to = range.To ?? payload.LongLength - 1;
                var content = payload[(int)from..((int)to + 1)];
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(content)
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.LongLength);
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveRequests);
                if (value <= current
                    || Interlocked.CompareExchange(ref _maximumActiveRequests, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
