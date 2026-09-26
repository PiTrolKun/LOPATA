using System.IO;
using System.Text;

namespace AIHub.Services;

internal sealed record LiteraryModelMemoryMetadata(string Architecture, int BlockCount, int ModelContextTokens,
    int PredictionLayers, long FileBytes)
{
    // The two-layer policy was validated for the installed dense hybrid Qwen backend.
    public bool SupportsRamReserve => Architecture == "qwen35" && BlockCount is >= 4 and <= 4096
        && PredictionLayers >= 0 && PredictionLayers < BlockCount - 2 && ModelContextTokens > 512;
    public int ReserveGpuLayers => SupportsRamReserve ? checked(BlockCount + 1 - 2)
        : throw new InvalidDataException("The selected model has no verified RAM reserve profile.");

    public static LiteraryModelMemoryMetadata Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    internal static LiteraryModelMemoryMetadata Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!stream.CanSeek || reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is < 2 or > 3)
            throw new InvalidDataException("Invalid GGUF header for memory planning.");
        _ = reader.ReadUInt64();
        var count = reader.ReadUInt64();
        if (count > 10000) throw new InvalidDataException("Unreasonable GGUF metadata count.");
        string? architecture = null;
        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        for (ulong index = 0; index < count; index++)
        {
            var key = ReadString(reader, 65536);
            var type = reader.ReadUInt32();
            if (key == "general.architecture")
            {
                if (type != 8 || architecture is not null) throw new InvalidDataException("Invalid GGUF architecture.");
                architecture = ReadString(reader, 128);
            }
            else if (key.EndsWith(".block_count", StringComparison.Ordinal)
                || key.EndsWith(".context_length", StringComparison.Ordinal)
                || key.EndsWith(".nextn_predict_layers", StringComparison.Ordinal))
            {
                var value = type switch
                {
                    4 => reader.ReadUInt32(),
                    10 => reader.ReadUInt64(),
                    _ => throw new InvalidDataException("Invalid GGUF memory metadata type.")
                };
                if (!values.TryAdd(key, value)) throw new InvalidDataException("Duplicate GGUF memory metadata.");
            }
            else SkipValue(reader, type, 0);
            CheckPosition(reader);
        }
        if (string.IsNullOrWhiteSpace(architecture)
            || !values.TryGetValue(architecture + ".block_count", out var blocks)
            || !values.TryGetValue(architecture + ".context_length", out var context)
            || blocks is 0 or > 4096 || context is 0 or > int.MaxValue)
            throw new InvalidDataException("Missing or invalid GGUF memory metadata.");
        values.TryGetValue(architecture + ".nextn_predict_layers", out var prediction);
        if (prediction >= blocks) throw new InvalidDataException("Invalid GGUF prediction-layer count.");
        return new(architecture, (int)blocks, (int)context, (int)prediction, stream.Length);
    }

    private static string ReadString(BinaryReader reader, ulong maximum)
    {
        var length = reader.ReadUInt64();
        if (length > maximum) throw new InvalidDataException("Unreasonable GGUF metadata string.");
        CheckBytes(reader, length);
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static void SkipValue(BinaryReader reader, uint type, int depth)
    {
        var width = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
        if (width > 0) { Skip(reader, (ulong)width); return; }
        if (type == 8)
        {
            var length = reader.ReadUInt64();
            if (length > 16 * 1024 * 1024) throw new InvalidDataException("Unreasonable GGUF metadata string.");
            Skip(reader, length); return;
        }
        if (type != 9 || depth != 0) throw new InvalidDataException("Invalid GGUF metadata type.");
        var element = reader.ReadUInt32(); var count = reader.ReadUInt64();
        if (count > 2_000_000) throw new InvalidDataException("Unreasonable GGUF metadata array.");
        for (ulong index = 0; index < count; index++) SkipValue(reader, element, depth + 1);
    }

    private static void Skip(BinaryReader reader, ulong length)
    {
        CheckBytes(reader, length);
        reader.BaseStream.Seek((long)length, SeekOrigin.Current);
    }
    private static void CheckBytes(BinaryReader reader, ulong length)
    {
        if (length > (ulong)Math.Max(0, reader.BaseStream.Length - reader.BaseStream.Position)
            || length > 64 * 1024 * 1024 || reader.BaseStream.Position > 64 * 1024 * 1024 - (long)length)
            throw new InvalidDataException("Incomplete or excessive GGUF metadata.");
    }
    private static void CheckPosition(BinaryReader reader)
    {
        if (reader.BaseStream.Position > 64 * 1024 * 1024) throw new InvalidDataException("Excessive GGUF metadata.");
    }
}
