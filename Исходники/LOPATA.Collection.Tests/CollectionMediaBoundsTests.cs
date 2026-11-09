using System.Buffers.Binary;
using System.Text;

namespace Lopata.Collection.Tests;

[TestClass]
public sealed class CollectionMediaBoundsTests
{
    private const string Origin = "{\"Program\":\"LOPATA\",\"Schema\":1,\"Scenario\":\"screen-capture/video\"}";

    [TestMethod]
    public void GifRequiresACommentExtensionAndIgnoresUnrelatedComments()
    {
        using var file = new FileFixture("gif");
        using (var bytes = new MemoryStream())
        {
            bytes.Write("GIF89a"u8); bytes.Write([1, 0, 1, 0, 0, 0, 0]);
            Comment(bytes, "An ordinary comment about LOPATA"); Comment(bytes, Origin); bytes.WriteByte(0x3b);
            File.WriteAllBytes(file.Path, bytes.ToArray());
        }
        Assert.AreEqual("screen-capture/video", CollectionOriginReader.Read(file.Path)?.Scenario);
        File.WriteAllBytes(file.Path, Encoding.UTF8.GetBytes("GIF89a" + Origin));
        Assert.IsNull(CollectionOriginReader.Read(file.Path));
    }

    [TestMethod]
    public void MatroskaRequiresStructuredSimpleTagsAndSkipsMediaPayload()
    {
        using var file = new FileFixture("mkv");
        var header = Ebml(0x1a45dfa3, Ebml(0x4282, "matroska"u8.ToArray()));
        var tag = Ebml(0x1254c367, Ebml(0x7373, Ebml(0x67c8, Combine(Ebml(0x45a3, "COMMENT"u8.ToArray()), Ebml(0x4487, Encoding.UTF8.GetBytes(Origin))))));
        File.WriteAllBytes(file.Path, Combine(header, Ebml(0x18538067, Combine(Ebml(0x1f43b675, new byte[3 * 1024 * 1024]), tag))));
        Assert.AreEqual("screen-capture/video", CollectionOriginReader.Read(file.Path)?.Scenario);
        File.WriteAllBytes(file.Path, Combine(header, Ebml(0x18538067, Ebml(0x1941a469, Encoding.UTF8.GetBytes(Origin)))));
        Assert.IsNull(CollectionOriginReader.Read(file.Path));
        File.WriteAllBytes(file.Path, Combine(header, Ebml(0x18538067, Ebml(0x1254c367, Combine(Ebml(0x45a3, "COMMENT"u8.ToArray()), Ebml(0x4487, Encoding.UTF8.GetBytes(Origin)))))));
        Assert.IsNull(CollectionOriginReader.Read(file.Path), "Text outside a SimpleTag is not origin metadata.");
    }

    [TestMethod]
    public void MidiTrackNamesAndBareAppJsonDoNotEstablishOrigin()
    {
        using var file = new FileFixture("mid");
        var text = "{\"app\":\"LOPATA\",\"schema\":1,\"version\":\"0.7.5\",\"operation\":\"music.melody.export\"}";
        var payload = Encoding.UTF8.GetBytes(text);
        using (var bytes = new MemoryStream())
        {
            bytes.Write("MThd"u8); bytes.Write([0, 0, 0, 6, 0, 0, 0, 1, 1, 224]);
            bytes.Write("MTrk"u8); Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(payload.Length + 8)); bytes.Write(size);
            bytes.Write([0, 255, 3, (byte)payload.Length]); bytes.Write(payload); bytes.Write([0, 255, 47, 0]);
            File.WriteAllBytes(file.Path, bytes.ToArray());
        }
        Assert.IsNull(CollectionOriginReader.Read(file.Path));
        var content = File.ReadAllBytes(file.Path); content[24] = 1; File.WriteAllBytes(file.Path, content);
        Assert.AreEqual("music/melody", CollectionOriginReader.Read(file.Path)?.Scenario);
        content[24] = 3; content[18] = 0x7f; File.WriteAllBytes(file.Path, content);
        Assert.IsNull(CollectionOriginReader.Read(file.Path));
    }

    private static void Comment(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); output.Write([0x21, 0xfe, (byte)bytes.Length]); output.Write(bytes); output.WriteByte(0);
    }
    private static byte[] Combine(params byte[][] values) => values.SelectMany(v => v).ToArray();
    private static byte[] Ebml(uint id, byte[] value)
    {
        using var output = new MemoryStream(); Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, id);
        var start = 0; while (start < 3 && number[start] == 0) start++; output.Write(number[start..]);
        var length = 1; while ((ulong)value.Length >= (1UL << (7 * length)) - 1) length++;
        ulong size = (uint)value.Length | (1UL << (7 * length));
        for (var index = length - 1; index >= 0; index--) output.WriteByte((byte)(size >> (8 * index)));
        output.Write(value); return output.ToArray();
    }
    private sealed class FileFixture(string extension) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + extension);
        public void Dispose() => File.Delete(Path);
    }
}
