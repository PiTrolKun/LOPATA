using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AIHub.Services.VideoEditor;

public sealed record VideoFileRequest(string Path)
{
    public VideoFileRequest Validate()
    {
        if (!System.IO.Path.IsPathFullyQualified(Path) || Path.Length > 32760 || Path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0
            || !System.IO.Path.GetExtension(Path).Equals(".lopataVideo", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("VideoEditor.InvalidProject");
        return this with { Path = System.IO.Path.GetFullPath(Path) };
    }
    public static VideoFileRequest? Parse(string[] args)
    {
        if (args is ["--video-project", "--", var path]) return new VideoFileRequest(path).Validate();
        if (args.Contains("--video-project", StringComparer.Ordinal)) throw new InvalidDataException("VideoEditor.InvalidProject");
        return null;
    }
}
internal static class VideoFileProtocol
{
    internal const byte Marker = 5;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static async Task WriteAsync(Stream stream, VideoFileRequest request, CancellationToken token)
    {
        var bytes = Utf8.GetBytes(request.Validate().Path); var header = new byte[5]; header[0] = Marker;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    internal static async Task<VideoFileRequest> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 131040) throw new InvalidDataException("VideoEditor.InvalidProject");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token); return new VideoFileRequest(Utf8.GetString(bytes)).Validate();
    }
}
