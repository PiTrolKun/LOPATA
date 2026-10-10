using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Per-user, per-installation instance. A dev checkout cannot take over the installed copy.</summary>
internal sealed class SingleApplicationInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly TaskCompletionSource<Func<ImageShellRequest, Task>> _imageHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Func<AudioShellRequest, Task>> _audioHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? OpenRequested;
    private int _pendingOpen;

    private SingleApplicationInstance(Mutex mutex, string pipe)
    { _mutex = mutex; _pipe = pipe; _server = ListenAsync(); }

    public static SingleApplicationInstance? Acquire(bool background, ImageShellRequest? imageRequest = null, AudioShellRequest? audioRequest = null)
    {
        var identity = Environment.UserDomainName + "\\" + Environment.UserName + "|" + Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant();
        var name = "LOPATA-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        var mutex = new Mutex(true, @"Local\" + name, out var first);
        if (first) return new(mutex, name);
        mutex.Dispose();
        if (audioRequest is not null)
        {
            SendAudioAsync(name, audioRequest).GetAwaiter().GetResult();
        }
        else if (imageRequest is not null)
        {
            SendImageAsync(name, imageRequest).GetAwaiter().GetResult();
        }
        else if (!background)
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            pipe.WriteByte(1); pipe.Flush();
        }
        return null;
    }

    public void SetImageShellHandler(Func<ImageShellRequest, Task> handler)
        => _imageHandler.TrySetResult(handler);

    public void SetAudioShellHandler(Func<AudioShellRequest, Task> handler) => _audioHandler.TrySetResult(handler);

    private static async Task SendAudioAsync(string name, AudioShellRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await AudioShellProtocol.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        var answer = new byte[1]; await pipe.ReadExactlyAsync(answer, timeout.Token).ConfigureAwait(false);
        if (answer[0] != ImageShellProtocol.Accepted) throw new IOException("Audio shell request was not accepted.");
    }

    private static async Task SendImageAsync(string name, ImageShellRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await ImageShellProtocol.WriteAsync(pipe, request, timeout.Token).ConfigureAwait(false);
        var answer = new byte[1];
        await pipe.ReadExactlyAsync(answer, timeout.Token).ConfigureAwait(false);
        if (answer[0] != ImageShellProtocol.Accepted)
            throw new IOException("The running application did not accept the image shell request.");
    }

    public void DeliverPendingOpen()
    { if (Interlocked.Exchange(ref _pendingOpen, 0) != 0) OpenRequested?.Invoke(); }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var request = new byte[1];
                using var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); read.CancelAfter(TimeSpan.FromSeconds(5));
                if (await pipe.ReadAsync(request, read.Token).ConfigureAwait(false) != 1) continue;
                if (request[0] == 1)
                {
                    Interlocked.Exchange(ref _pendingOpen, 1);
                    if (OpenRequested is not null) DeliverPendingOpen();
                }
                else if (request[0] == ImageShellProtocol.RequestMarker)
                {
                    read.CancelAfter(TimeSpan.FromSeconds(30));
                    var handler = await _imageHandler.Task.WaitAsync(read.Token).ConfigureAwait(false);
                    await ImageShellProtocol.ReceiveAsync(pipe, handler, read.Token).ConfigureAwait(false);
                }
                else if (request[0] == AudioShellProtocol.RequestMarker)
                {
                    read.CancelAfter(TimeSpan.FromSeconds(30));
                    var handler = await _audioHandler.Task.WaitAsync(read.Token).ConfigureAwait(false);
                    await AudioShellProtocol.ReceiveAsync(pipe, handler, read.Token).ConfigureAwait(false);
                }
                else throw new InvalidDataException("Unknown application instance request.");
            }
            catch (OperationCanceledException)
            { if (!_stop.IsCancellationRequested) OwnedProcessRegistry.Log("instance_pipe_timeout", "Application"); }
            catch (Exception error)
            { OwnedProcessRegistry.Log("instance_pipe_failed", "Application", detail: error.GetType().Name); }
        }
    }

    public void Dispose()
    { _stop.Cancel(); _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
