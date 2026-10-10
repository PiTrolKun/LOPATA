namespace Lopata.Updates;

// Bound each period without network progress, not the duration of a large download.
internal sealed class UpdateHttpTransfer(HttpClient http, TimeSpan idleTimeout)
{
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(idleTimeout);
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new UpdateNetworkException("No response from the update server within the network timeout.", error); }
    }

    public async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(idleTimeout);
        try { return await stream.ReadAsync(buffer, timeout.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new UpdateNetworkException("The update server stopped sending data.", error); }
        catch (IOException error)
        { throw new UpdateNetworkException("The update server interrupted the response.", error); }
    }
}

internal sealed class UpdateNetworkException(string message, Exception inner) : IOException(message, inner);
