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
    internal static async Task WriteAsync(Stream stream, AudioShellRequest request, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request.Validate(), Json);
        if (payload.Length > ImageShellProtocol.MaximumMessageBytes) throw new InvalidDataException("Audio shell message is too large.");
        var header = new byte[5]; header[0] = RequestMarker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    internal static async Task ReceiveAsync(Stream stream, Func<AudioShellRequest, Task> accept, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > ImageShellProtocol.MaximumMessageBytes) throw new InvalidDataException("Invalid audio shell message.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        var request = (JsonSerializer.Deserialize<AudioShellRequest>(bytes, Json) ?? throw new InvalidDataException("Missing audio request.")).Validate();
        await accept(request).WaitAsync(token);
        await stream.WriteAsync(new byte[] { ImageShellProtocol.Accepted }, token); await stream.FlushAsync(token);
    }
}
