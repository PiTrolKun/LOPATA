using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Bounded protocol on the existing per-user instance pipe. Byte 1 remains the legacy open request.</summary>
internal static class ImageShellProtocol
{
    internal const byte RequestMarker = 2;
    internal const byte Accepted = 1;
    internal const int MaximumMessageBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ImageShellOperation>(allowIntegerValues: false) }
    };

    internal static async Task WriteAsync(Stream stream, ImageShellRequest request, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request.Validate(), Json);
        if (payload.Length is < 1 or > MaximumMessageBytes)
            throw new InvalidDataException("Image shell request exceeds the message limit.");
        var header = new byte[5]; header[0] = RequestMarker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // The caller already consumed the one-byte request marker.
    internal static async Task<ImageShellRequest> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumMessageBytes)
            throw new InvalidDataException("Invalid image shell request length.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return (JsonSerializer.Deserialize<ImageShellRequest>(payload, Json)
            ?? throw new InvalidDataException("Empty image shell request.")).Validate();
    }

    internal static async Task ReceiveAsync(Stream stream, Func<ImageShellRequest, Task> persist, CancellationToken token)
    {
        var request = await ReadAsync(stream, token).ConfigureAwait(false);
        // This is an acknowledgement of durable queue storage, never merely receipt of bytes.
        await persist(request).WaitAsync(token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { Accepted }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
