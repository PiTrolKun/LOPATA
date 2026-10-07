using System.IO;
using System.Text;

namespace AIHub.Services;

internal sealed record LiteraryModelMemoryMetadata(string Architecture, int BlockCount, int ModelContextTokens,
    int PredictionLayers, long FileBytes)
{
    public int EmbeddingLength { get; init; }
    public int HeadCount { get; init; }
    public int KvHeadCount { get; init; }
    public int KeyLength { get; init; }
    public int ValueLength { get; init; }
    public int KeyLengthSwa { get; init; }
    public int ValueLengthSwa { get; init; }
    public IReadOnlyList<int>? HeadCounts { get; init; }
    public IReadOnlyList<int>? KvHeadCounts { get; init; }
    public int SsmConvKernel { get; init; }
    public int SsmInnerSize { get; init; }
    public int SsmStateSize { get; init; }
    public int SsmGroupCount { get; init; }
    public int FullAttentionInterval { get; init; }
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
        var arrays = new Dictionary<string, int[]>(StringComparer.Ordinal);
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
            else if (key.EndsWith(".embedding_length", StringComparison.Ordinal)
                || key.EndsWith(".attention.head_count", StringComparison.Ordinal)
                || key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)
                || key.EndsWith(".attention.key_length", StringComparison.Ordinal)
                || key.EndsWith(".attention.value_length", StringComparison.Ordinal)
                || key.EndsWith(".attention.key_length_swa", StringComparison.Ordinal)
                || key.EndsWith(".attention.value_length_swa", StringComparison.Ordinal)
                || key.EndsWith(".ssm.conv_kernel", StringComparison.Ordinal)
                || key.EndsWith(".ssm.inner_size", StringComparison.Ordinal)
                || key.EndsWith(".ssm.state_size", StringComparison.Ordinal)
                || key.EndsWith(".ssm.group_count", StringComparison.Ordinal)
                || key.EndsWith(".full_attention_interval", StringComparison.Ordinal))
            {
                if (values.ContainsKey(key) || arrays.ContainsKey(key))
                    throw new InvalidDataException("Duplicate GGUF memory metadata.");
                if (type is 4 or 10)
                {
                    var value = type == 4 ? reader.ReadUInt32() : reader.ReadUInt64();
                    if (!values.TryAdd(key, value)) throw new InvalidDataException("Duplicate GGUF memory metadata.");
                }
                else if (type == 9 && (key.EndsWith(".attention.head_count", StringComparison.Ordinal)
                    || key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)))
                {
                    var element = reader.ReadUInt32(); var length = reader.ReadUInt64();
                    if (element is not (4 or 10) || length is 0 or > 4096)
                        throw new InvalidDataException("Invalid per-layer attention dimensions.");
                    var dimensions = new int[(int)length];
                    for (var layer = 0; layer < dimensions.Length; layer++)
                    {
                        var value = element == 4 ? reader.ReadUInt32() : reader.ReadUInt64();
                        if (value > 1_000_000) throw new InvalidDataException("Unreasonable per-layer attention dimension.");
                        dimensions[layer] = (int)value;
                    }
                    arrays.Add(key, dimensions);
                }
                else throw new InvalidDataException("Unsupported GGUF memory dimension type.");
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
        if (arrays.Any(pair => pair.Value.Length != (int)blocks))
            throw new InvalidDataException("Per-layer attention dimension count differs from block count.");
        int Dimension(string suffix) => values.TryGetValue(architecture + suffix, out var value)
            && value is > 0 and <= 1_000_000 ? (int)value : 0;
        int[]? Layers(string suffix) => arrays.GetValueOrDefault(architecture + suffix);
        var heads = Layers(".attention.head_count");
        var kvHeads = Layers(".attention.head_count_kv");
        var hasKvHeads = values.ContainsKey(architecture + ".attention.head_count_kv") || kvHeads is not null;
        return new(architecture, (int)blocks, (int)context, (int)prediction, stream.Length)
        {
            EmbeddingLength = Dimension(".embedding_length"), HeadCount = heads?[0] ?? Dimension(".attention.head_count"),
            // b9442 defaults a missing KV-head count to the query-head count (MHA).
            // An explicitly invalid value or array must never take this default.
            KvHeadCount = hasKvHeads ? kvHeads?[0] ?? Dimension(".attention.head_count_kv")
                : heads?[0] ?? Dimension(".attention.head_count"),
            HeadCounts = heads, KvHeadCounts = hasKvHeads ? kvHeads : heads,
            KeyLength = Dimension(".attention.key_length"),
            ValueLength = Dimension(".attention.value_length"),
            KeyLengthSwa = Dimension(".attention.key_length_swa"), ValueLengthSwa = Dimension(".attention.value_length_swa"),
            SsmConvKernel = Dimension(".ssm.conv_kernel"), SsmInnerSize = Dimension(".ssm.inner_size"),
            SsmStateSize = Dimension(".ssm.state_size"), SsmGroupCount = Dimension(".ssm.group_count"),
            FullAttentionInterval = Dimension(".full_attention_interval")
        };
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
