using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AIHub.Services;

/// <summary>Per-user, per-installation instance. A dev checkout cannot take over the installed copy.</summary>
internal sealed class SingleApplicationInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    public event Action? OpenRequested;
    private int _pendingOpen;

    private SingleApplicationInstance(Mutex mutex, string pipe)
    { _mutex = mutex; _pipe = pipe; _server = ListenAsync(); }

    public static SingleApplicationInstance? Acquire(bool background)
    {
        var identity = Environment.UserDomainName + "\\" + Environment.UserName + "|" + Path.GetFullPath(AppContext.BaseDirectory).ToUpperInvariant();
        var name = "LOPATA-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        var mutex = new Mutex(true, @"Local\" + name, out var first);
        if (first) return new(mutex, name);
        mutex.Dispose();
        if (!background)
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            pipe.WriteByte(1); pipe.Flush();
        }
        return null;
    }

    public void DeliverPendingOpen()
    { if (Interlocked.Exchange(ref _pendingOpen, 0) != 0) OpenRequested?.Invoke(); }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var request = new byte[1];
                using var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); read.CancelAfter(TimeSpan.FromSeconds(5));
                if (await pipe.ReadAsync(request, read.Token) == 1 && request[0] == 1)
                {
                    Interlocked.Exchange(ref _pendingOpen, 1);
                    if (OpenRequested is not null) DeliverPendingOpen();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException error) { OwnedProcessRegistry.Log("instance_pipe_failed", "Application", detail: error.GetType().Name); return; }
        }
    }

    public void Dispose()
    { _stop.Cancel(); _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
