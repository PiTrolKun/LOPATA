using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace LOPATA.Updates.Tests;

internal sealed class ParallelDownloadHandler(Dictionary<string, byte[]> payloads) : HttpMessageHandler
{
    public readonly ConcurrentQueue<(string Id, long From, long To)> Requests = new();
    public bool NoRanges, WrongRange, Corrupt, BreakStream;
    public int Peak, Active;
    public long Received;
    private readonly object _sync = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var id = request.RequestUri!.Segments[^1];
        var bytes = payloads[id];
        var range = NoRanges ? null : request.Headers.Range?.Ranges.Single();
        var from = range?.From ?? 0;
        var to = range?.To ?? bytes.Length - 1;
        Requests.Enqueue((id, from, to));
        var data = bytes[(int)from..((int)to + 1)];
        if (Corrupt && data.Length > 1) data[0] ^= 1;
        lock (_sync) { Active++; Peak = Math.Max(Peak, Active); }
        var stream = new DelayedStream(data, this, BreakStream && data.Length > 1);
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = data.Length;
        if (range is not null)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                WrongRange && data.Length > 1 ? from + 1 : from, WrongRange && data.Length > 1 ? to + 1 : to, bytes.Length);
        return Task.FromResult(response);
    }

    private sealed class DelayedStream(byte[] bytes, ParallelDownloadHandler owner, bool interrupt) : MemoryStream(bytes)
    {
        private bool _closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(2, cancellationToken);
            if (interrupt && Position > 0) throw new IOException("Simulated broken connection");
            var count = await base.ReadAsync(buffer[..Math.Min(1024, buffer.Length)], cancellationToken);
            Interlocked.Add(ref owner.Received, count);
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (!_closed) { _closed = true; lock (owner._sync) owner.Active--; }
            base.Dispose(disposing);
        }
    }
}
