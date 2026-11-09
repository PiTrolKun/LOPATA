using System.Buffers.Binary;
using System.Text;

namespace Lopata.Collection;

internal static class CollectionVideoOrigin
{
    public static CollectionOrigin? Read(Stream stream) => ReadBoxes(stream, stream.Length, 0);

    private static CollectionOrigin? ReadBoxes(Stream stream, long end, int depth)
    {
        if (depth > 10) return null;
        Span<byte> header = stackalloc byte[8];
        for (var count = 0; count < 8192 && stream.Position + 8 <= end; count++)
        {
            var start = stream.Position;
            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header[4..]);
            if (size == 1)
            {
                stream.ReadExactly(header);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header);
                if (large > long.MaxValue) return null;
                size = (long)large;
            }
            if (size == 0) size = end - start;
            if (size < stream.Position - start || size > end - start) return null;
            var boxEnd = start + size;
            if (type == "meta")
            {
                if (boxEnd - stream.Position <= CollectionOriginReader.MetadataLimit)
                {
                    var data = new byte[checked((int)(boxEnd - stream.Position))]; stream.ReadExactly(data);
                    if (ReadMeta(data) is { } result) return result;
                }
            }
            else if (type is "moov" or "udta")
            {
                if (ReadBoxes(stream, boxEnd, depth + 1) is { } result) return result;
            }
            stream.Position = boxEnd;
        }
        return null;
    }

    private static CollectionOrigin? ReadMeta(byte[] bytes)
    {
        var keys = new List<string>(); ReadOnlyMemory<byte> items = default;
        foreach (var (type, value) in Boxes(bytes.AsMemory(), 4))
        {
            if (type == "keys" && value.Length >= 8)
            {
                var span = value.Span; var count = BinaryPrimitives.ReadUInt32BigEndian(span[4..]); var offset = 8;
                if (count > 4096) return null;
                for (var index = 0U; index < count; index++)
                {
                    if (offset + 8 > span.Length) return null;
                    var size = BinaryPrimitives.ReadUInt32BigEndian(span[offset..]);
                    if (size < 8 || size > span.Length - offset) return null;
                    keys.Add(Encoding.UTF8.GetString(span.Slice(offset + 8, (int)size - 8)));
                    offset += (int)size;
                }
            }
            if (type == "ilst") items = value;
        }
        if (items.IsEmpty) return null;
        foreach (var (type, item) in Boxes(items, 0))
        {
            var typeBytes = Encoding.Latin1.GetBytes(type);
            var index = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
            string? key = type == "©cmt" ? "comment" : index is > 0 && index <= keys.Count ? keys[(int)index - 1] : null;
            string? value = null;
            foreach (var (childType, childValue) in Boxes(item, 0))
            {
                if (childType == "name" && childValue.Length >= 4) key = Encoding.UTF8.GetString(childValue.Span[4..]);
                if (childType == "data" && childValue.Length >= 8) value = Encoding.UTF8.GetString(childValue.Span[8..]);
            }
            if (key is "LOPATA_VIDEO" or "LOPATA_PROVENANCE" or "LOPATA.Provenance" or "comment" or "COMMENT" && value is not null
                && CollectionMediaOrigin.JsonValue(value) is { } origin) return origin;
        }
        return null;
    }

    private static IEnumerable<(string Type, ReadOnlyMemory<byte> Value)> Boxes(ReadOnlyMemory<byte> data, int offset)
    {
        for (var count = 0; count < 8192 && offset + 8 <= data.Length; count++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]);
            if (size < 8 || size > data.Length - offset) yield break;
            var type = Encoding.Latin1.GetString(data.Span.Slice(offset + 4, 4));
            yield return (type, data.Slice(offset + 8, (int)size - 8));
            offset += (int)size;
        }
    }
}
