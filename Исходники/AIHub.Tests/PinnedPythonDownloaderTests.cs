using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PinnedPythonDownloaderTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6];
    private static PinnedPythonArtifact Artifact => new("test", "1", "test.whl", Payload.Length,
        Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant(), new Uri("https://files.pythonhosted.org/test.whl"));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handle(request));
    }

    [TestMethod]
    public async Task CancellationRetainsOnlyPartialBytesAndNextCallResumes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-python-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancelled = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(request => new(HttpStatusCode.OK)
        { Content = new StreamContent(new InterruptedStream(cancelled)), RequestMessage = request }));
        try
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PinnedPythonDownloader.AcquireAsync(
                [Artifact], root, client, null, cancelled.Token));
            Assert.IsFalse(File.Exists(Path.Combine(root, "test.whl")));
            CollectionAssert.AreEqual(Payload[..2], File.ReadAllBytes(Path.Combine(root, "test.whl.part")));
            using var resumed = new HttpClient(new Handler(request =>
            {
                if (request.Headers.Range?.Ranges.Single().From != 2) throw new InvalidOperationException("Wrong interrupted offset.");
                var reply = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(Payload[2..]), RequestMessage = request };
                reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
                return reply;
            }));
            var paths = await PinnedPythonDownloader.AcquireAsync([Artifact], root, resumed, null, CancellationToken.None);
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(paths["test.whl"]));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class InterruptedStream(CancellationTokenSource cancellation) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_sent) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            _sent = true; Payload.AsMemory(0, 2).CopyTo(buffer); return ValueTask.FromResult(2);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task InterruptedArtifactResumesOrSafelyRestartsWhenRangeIsIgnored(bool resume)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-python-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var part = Path.Combine(root, "test.whl.part"); await File.WriteAllBytesAsync(part, Payload[..2]);
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Headers.Range?.Ranges.Single().From != 2) throw new InvalidOperationException("Missing resume offset.");
            var reply = new HttpResponseMessage(resume ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(resume ? Payload[2..] : Payload), RequestMessage = request };
            if (resume) reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
            return reply;
        }));
        try
        {
            var paths = await PinnedPythonDownloader.AcquireAsync([Artifact], root, client, null, CancellationToken.None);
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(paths["test.whl"]));
            Assert.IsFalse(File.Exists(part));
            using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("Verified cache must be offline.")));
            await PinnedPythonDownloader.AcquireAsync([Artifact], root, offline, null, CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("range")]
    [DataRow("oversize")]
    [DataRow("digest")]
    [DataRow("redirect")]
    public async Task CorruptOrUntrustedReplyNeverReplacesPreviousCache(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-python-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var cached = Path.Combine(root, "test.whl");
        var prior = new byte[] { 8, 9 }; await File.WriteAllBytesAsync(cached, prior);
        using var client = new HttpClient(new Handler(request =>
        {
            var payload = failure == "oversize" ? new byte[7] : new byte[6];
            var reply = new HttpResponseMessage(failure == "range" ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new ByteArrayContent(payload), RequestMessage = request };
            if (failure == "range") reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(1, 5, 6);
            if (failure == "redirect") reply.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.org/test.whl");
            return reply;
        }));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                PinnedPythonDownloader.AcquireAsync([Artifact], root, client, null, CancellationToken.None));
            CollectionAssert.AreEqual(prior, File.ReadAllBytes(cached));
        }
        finally { Directory.Delete(root, true); }
    }
}
