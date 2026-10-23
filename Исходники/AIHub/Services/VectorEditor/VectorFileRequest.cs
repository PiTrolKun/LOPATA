using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services.VectorEditor;

public sealed record VectorFileRequest(string Path)
{
    public VectorFileRequest Validate()
    {
        if (!System.IO.Path.IsPathFullyQualified(Path) || Path.Length > 32760 || Path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
            || !new[] { ".svg", ".lopatasvg" }.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant()))
            throw new InvalidDataException("Vector.Editor.Error.File");
        return this with { Path = System.IO.Path.GetFullPath(Path) };
    }
    public static VectorFileRequest? Parse(string[] args)
    {
        if (args is ["--svg", "--", var path]) return new VectorFileRequest(path).Validate();
        if (args.Contains("--svg", StringComparer.Ordinal)) throw new InvalidDataException("Vector.Editor.Error.File");
        return null;
    }
}

internal static class VectorFileProtocol
{
    internal const byte Marker = 4;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static async Task WriteAsync(Stream stream, VectorFileRequest request, CancellationToken token)
    {
        var bytes = Utf8.GetBytes(request.Validate().Path); var header = new byte[5]; header[0] = Marker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    internal static async Task<VectorFileRequest> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 131040) throw new InvalidDataException("Vector.Editor.Error.File");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return new VectorFileRequest(Utf8.GetString(bytes)).Validate();
    }
}
