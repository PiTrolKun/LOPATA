using System.Text;

namespace Lopata.Collection;

public sealed record CollectionItem(string Path, string Kind, string Scenario);

public static class CollectionSnapshot
{
    public const int MaxEntries = 20_000;
    public const int MaxBytes = 16 * 1024 * 1024;
    public const uint Magic = 0x3143504c;

    public static void Write(string directory, IReadOnlyCollection<CollectionItem> entries)
    {
        if (entries.Count > MaxEntries) throw new InvalidDataException("Collection has too many entries.");
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.Unicode, true))
        {
            writer.Write(Magic);
            writer.Write(1U);
            writer.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.Write((uint)entries.Count);
            foreach (var entry in entries)
            {
                WriteString(writer, entry.Path);
                WriteString(writer, entry.Kind);
                WriteString(writer, entry.Scenario);
            }
        }
        if (data.Length > MaxBytes) throw new InvalidDataException("Collection snapshot is too large.");
        CollectionFiles.WriteAtomic(Path.Combine(directory, "snapshot.bin"), data.ToArray());
    }

    public static IReadOnlyList<CollectionItem> Read(string directory)
    {
        var path = Path.Combine(directory, "snapshot.bin");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Collection snapshot is too large.");
        using var reader = new BinaryReader(stream, Encoding.Unicode);
        if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != 1) throw new InvalidDataException("Unsupported snapshot.");
        _ = reader.ReadInt64();
        var count = reader.ReadUInt32();
        if (count > MaxEntries) throw new InvalidDataException("Invalid snapshot count.");
        var result = new List<CollectionItem>((int)count);
        for (var index = 0; index < count; index++)
            result.Add(new(ReadString(reader), ReadString(reader), ReadString(reader)));
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected snapshot bytes.");
        return result;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Length > 32700 || value.Any(char.IsControl)) throw new InvalidDataException("Invalid snapshot string.");
        writer.Write((uint)value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static string ReadString(BinaryReader reader)
    {
        var count = reader.ReadUInt32();
        if (count > 32700) throw new InvalidDataException("Invalid snapshot string.");
        var bytes = reader.ReadBytes(checked((int)count * 2));
        if (bytes.Length != count * 2) throw new EndOfStreamException();
        var value = Encoding.Unicode.GetString(bytes);
        if (value.Any(char.IsControl)) throw new InvalidDataException("Invalid snapshot text.");
        return value;
    }
}
