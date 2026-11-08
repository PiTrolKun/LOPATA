using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services.OfficeReader;

public sealed record ReaderFileRequest(string Path)
{
    // Images and generic archives keep their existing Windows handlers. The private office
    // container is added only when its actual reader is implemented.
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(ReaderPreparation.OfficeExtensions
        .Concat(ReaderPreparation.BookExtensions).Concat([".pdf", ".djvu", ".djv", ".cbz", ".cbr"]).ToArray());

    public ReaderFileRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path) || Path.Length > 32760
            || Path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
            || !(Extensions.Contains(System.IO.Path.GetExtension(Path), StringComparer.OrdinalIgnoreCase)
                || Path.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Reader.Unsupported");
        return this with { Path = System.IO.Path.GetFullPath(Path) };
    }

    public static ReaderFileRequest? Parse(string[] args)
    {
        if (args is ["--reader", "--", var path]) return new ReaderFileRequest(path).Validate();
        if (args.Contains("--reader", StringComparer.Ordinal)) throw new InvalidDataException("Reader.Unsupported");
        // Windows also passes a bare file when the user browses directly to the executable.
        if (args is [var file] && !file.StartsWith("--", StringComparison.Ordinal)) return new ReaderFileRequest(file).Validate();
        return null;
    }
}

internal static class ReaderFileProtocol
{
    internal const byte Marker = 6;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static async Task WriteAsync(Stream stream, ReaderFileRequest request, CancellationToken token)
    {
        var bytes = Utf8.GetBytes(request.Validate().Path); var header = new byte[5]; header[0] = Marker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    internal static async Task<ReaderFileRequest> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 131040) throw new InvalidDataException("Reader.Unsupported");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return new ReaderFileRequest(Utf8.GetString(bytes)).Validate();
    }
}
