using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lopata.Island;

internal sealed record IslandJob(string Id, string Title, string Phase, bool CanPause, bool CanResume, double? Progress = null);
internal sealed record IslandTurn(string Role, string Text);
internal sealed record IslandRequest(string Action, string? Id = null, string? Text = null,
    string? PauseId = null, IslandTurn[]? History = null);
internal sealed record IslandResponse(string Result, IslandJob[]? Jobs = null, bool Busy = false,
    string? BlockingId = null, string? Text = null, bool? ModelActive = null, string? ModelFailure = null);

// Versioned, per-installation channel. It accepts only local Windows users matching the server.
internal static class IslandLink
{
    internal const int MaximumDraft = 16000;
    private const int MaximumFrame = 512 * 1024;
    internal const string CorePrefix = "LOPATA.Island.Core.";

    internal static string InstallationName(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar;
        var identity = Environment.UserDomainName + "\\" + Environment.UserName + "|" + path.ToUpperInvariant();
        return "LOPATA-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    internal static bool HostRunning(string directory)
    {
        if (!Mutex.TryOpenExisting(@"Local\" + InstallationName(directory), out var marker)) return false;
        marker.Dispose();
        return true;
    }

    internal static string CoreLeaseName(string directory) => @"Local\" + InstallationName(directory) + ".IslandCore";

    internal static async Task WaitForCoreRetirementAsync(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var name = CoreLeaseName(directory);
        while (Mutex.TryOpenExisting(name, out var marker))
        {
            marker.Dispose();
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private static string HostPipe(string directory) => InstallationName(directory) + ".Island.v1";
    internal static NamedPipeServerStream ListenHost(string directory) => Listen(HostPipe(directory), 4);
    internal static NamedPipeServerStream ListenCore(string name) => Listen(name, 1);

    private static NamedPipeServerStream Listen(string name, int maximum) =>
        new(name, PipeDirection.InOut, maximum, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    internal static NamedPipeClientStream Client(string name) =>
        new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    internal static async Task<IslandResponse> AskHostAsync(string directory, IslandRequest request, CancellationToken token)
    {
        await using var pipe = Client(HostPipe(directory));
        await pipe.ConnectAsync(1000, token).ConfigureAwait(false);
        await WriteAsync(pipe, request, token).ConfigureAwait(false);
        return await ReadAsync<IslandResponse>(pipe, token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream pipe, CancellationToken token) where T : class
    {
        var header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumFrame) throw new InvalidDataException("Invalid island message size.");
        var bytes = new byte[size];
        await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty island message.");
    }

    internal static async Task WriteAsync<T>(Stream pipe, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Island message too large.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, token).ConfigureAwait(false);
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }
}
