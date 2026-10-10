using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class BootstrapHttpTests
{
    [TestMethod]
    public async Task RealHttpRangesUseGlobalLimitAndResumeCancelledTransfer()
    {
        using var fixture = new UpdateFixture();
        var bytes = RandomNumberGenerator.GetBytes(25 * 1024 * 1024);
        await using var server = new RangeServer(bytes);
        using var http = new HttpClient(new LocalTransport(server.Port));
        var target = UpdateFixture.Manifest("0.5.1-beta", UpdateFixture.Entry("engine.bin", "fixture"));
        target = target with { Packages = [target.Packages[0] with { Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }] };
        var cache = Path.Combine(fixture.Folder, "cache");
        var downloader = new UpdatePackageDownloader(http, cache) { MaximumParallelConnections = 4 };
        using var stop = new CancellationTokenSource();
        var progress = new ImmediateProgress(p => { if (p.StoredBytes > 2 * 1024 * 1024) stop.Cancel(); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => downloader.DownloadManyAsync(target, target.Packages, progress, stop.Token));
        Assert.IsTrue(server.Peak > 1 && server.Peak <= 4, "Actual TCP range streams must be parallel and bounded.");
        var prefixBytes = server.Sent;
        var saved = Directory.GetFiles(cache, "*.part", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        Assert.IsTrue(saved > 0);
        downloader.MaximumParallelConnections = 2;
        var paths = await downloader.DownloadManyAsync(target, target.Packages);
        Assert.IsTrue(await UpdatePlanner.MatchesAsync(paths[target.Packages[0].Id], bytes.Length, target.Packages[0].Sha256));
        Assert.IsTrue(server.Sent - prefixBytes < bytes.Length, "The resumed real HTTP transfer must reuse saved ranges.");
        Assert.IsTrue(server.Starts.Any(s => s != 0 && s % (8 * 1024 * 1024) != 0));
    }

    private sealed class ImmediateProgress(Action<UpdateTransferProgress> action) : IProgress<UpdateTransferProgress>
    { public void Report(UpdateTransferProgress value) => action(value); }

    // Test-only transport: production manifests and URL validation remain unchanged.
    private sealed class LocalTransport(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{port}/package");
            return base.SendAsync(request, token);
        }
    }

    private sealed class RangeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        private readonly byte[] _bytes;
        private int _active;
        public int Peak;
        public long Sent;
        public ConcurrentBag<long> Starts { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public RangeServer(byte[] bytes) { _bytes = bytes; _listener.Start(); _accept = Accept(); }
        private async Task Accept()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                var active = Interlocked.Increment(ref _active);
                lock (_clients) Peak = Math.Max(Peak, active);
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    long from = 0, to = _bytes.Length - 1;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            var range = line[13..].Split('-'); from = long.Parse(range[0]);
                            if (range[1].Length > 0) to = long.Parse(range[1]);
                        }
                    Starts.Add(from);
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 206 Partial Content\r\nContent-Length: {to - from + 1}\r\nContent-Range: bytes {from}-{to}/{_bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    for (var position = from; position <= to;)
                    {
                        var count = (int)Math.Min(65536, to - position + 1);
                        await stream.WriteAsync(_bytes.AsMemory((int)position, count), _stop.Token);
                        Interlocked.Add(ref Sent, count); position += count;
                        if (count > 1) await Task.Delay(2, _stop.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
                finally { Interlocked.Decrement(ref _active); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _accept; _listener.Stop();
            await Task.WhenAll(_clients); _stop.Dispose();
        }
    }
}
