using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

internal static class AudioShellProtocol
{
    internal const byte RequestMarker = 3;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static Task WriteAsync(Stream stream, AudioShellRequest request, CancellationToken token) =>
        WriteMessageAsync(stream, RequestMarker, request.Validate(), token);
    internal static Task ReceiveAsync(Stream stream, Func<AudioShellRequest, Task> accept, CancellationToken token) =>
        ReceiveMessageAsync<AudioShellRequest>(stream, request => accept(request.Validate()), token);

    internal static async Task WriteMessageAsync<T>(Stream stream, byte marker, T request, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (payload.Length > ImageShellProtocol.MaximumMessageBytes) throw new InvalidDataException("Media shell message is too large.");
        var header = new byte[5]; header[0] = marker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    internal static async Task ReceiveMessageAsync<T>(Stream stream, Func<T, Task> accept, CancellationToken token) where T : class
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > ImageShellProtocol.MaximumMessageBytes) throw new InvalidDataException("Invalid media shell message.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        var request = JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Missing media shell request.");
        await accept(request).WaitAsync(token);
        await stream.WriteAsync(new byte[] { ImageShellProtocol.Accepted }, token); await stream.FlushAsync(token);
    }
}
