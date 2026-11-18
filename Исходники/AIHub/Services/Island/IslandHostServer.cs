using System.IO;
using System.Text.Json;
using Lopata.Island;

namespace AIHub.Services;

internal sealed class IslandHostServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    internal IslandHostServer(Func<IslandRequest, CancellationToken, Task<IslandResponse>> dispatch)
    {
        _server = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ListenAsync(dispatch, _stop.Token)));
    }

    private static async Task ListenAsync(Func<IslandRequest, CancellationToken, Task<IslandResponse>> dispatch,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = IslandLink.ListenHost(AppContext.BaseDirectory);
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                var request = await IslandLink.ReadAsync<IslandRequest>(pipe, requestTimeout.Token).ConfigureAwait(false);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var disconnect = WatchClientAsync(pipe, requestTimeout);
                try
                {
                    var response = await dispatch(request, requestTimeout.Token).ConfigureAwait(false);
                    await IslandLink.WriteAsync(pipe, response, requestTimeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    await requestTimeout.CancelAsync().ConfigureAwait(false);
                    await disconnect.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            {
                OwnedProcessRegistry.Log("island_link_request_failed", "NabyScape", detail: error.GetType().Name);
            }
        }
    }

    private static async Task WatchClientAsync(Stream pipe, CancellationTokenSource request)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], request.Token).ConfigureAwait(false); }
        catch (IOException) { }
        catch (OperationCanceledException) { return; }
        await request.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ = _server.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
