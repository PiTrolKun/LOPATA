using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Storage spans are upper bounds, including tensor padding; no quantization-size guesses.</summary>
internal sealed record LlamaLayerPlacement(long FixedBytes, IReadOnlyList<long> LayerBytes)
{
    internal static LlamaLayerPlacement Read(string path, LiteraryModelMemoryMetadata metadata)
    {
        if (metadata.TensorCount is 0 or > 1_000_000) throw new InvalidDataException("Invalid GGUF tensor count.");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = metadata.TensorDirectoryOffset;
        var tensors = new List<(ulong Offset, int Layer)>();
        for (ulong index = 0; index < metadata.TensorCount; index++)
        {
            var length = reader.ReadUInt64();
            if (length is 0 or > 65536) throw new InvalidDataException("Invalid tensor name.");
            var name = Encoding.UTF8.GetString(reader.ReadBytes((int)length));
            var dimensions = reader.ReadUInt32();
            if (dimensions is 0 or > 4) throw new InvalidDataException("Invalid tensor dimensions.");
            for (var dimension = 0; dimension < dimensions; dimension++)
                if (reader.ReadUInt64() == 0) throw new InvalidDataException("Empty tensor dimension.");
            _ = reader.ReadUInt32(); // Backend validates the quantization; offsets bound our budget.
            var offset = reader.ReadUInt64();
            var layer = -1;
            if (name.StartsWith("blk.", StringComparison.Ordinal))
            {
                var end = name.IndexOf('.', 4);
                if (end <= 4 || !int.TryParse(name.AsSpan(4, end - 4), out layer)
                    || layer < 0 || layer >= metadata.BlockCount) throw new InvalidDataException("Invalid tensor layer.");
            }
            tensors.Add((offset, layer));
            if (stream.Position > 64 * 1024 * 1024) throw new InvalidDataException("Excessive tensor directory.");
        }
        var dataStart = checked((stream.Position + metadata.Alignment - 1) / metadata.Alignment * metadata.Alignment);
        var dataBytes = stream.Length - dataStart;
        if (dataBytes <= 0) throw new InvalidDataException("Missing GGUF tensor data.");
        var sorted = tensors.OrderBy(t => t.Offset).ToArray();
        var layers = new long[metadata.BlockCount];
        for (var index = 0; index < sorted.Length; index++)
        {
            var end = index + 1 < sorted.Length ? sorted[index + 1].Offset : (ulong)dataBytes;
            if (sorted[index].Offset >= (ulong)dataBytes || end <= sorted[index].Offset || end > (ulong)dataBytes)
                throw new InvalidDataException("Invalid tensor storage span.");
            if (sorted[index].Layer >= 0)
                layers[sorted[index].Layer] = checked(layers[sorted[index].Layer] + (long)(end - sorted[index].Offset));
        }
        if (layers.Any(bytes => bytes <= 0)) throw new InvalidDataException("Incomplete layer placement data.");
        return new(stream.Length - layers.Sum(), layers);
    }

    internal int HybridLayers(RuntimeDevice device, long kvBytes)
    {
        if (LayerBytes.Count < 2 || kvBytes < 0) throw new InvalidDataException("Invalid hybrid placement budget.");
        // Full KV/compute/fixed tensors are counted on GPU, even if native placement keeps some on CPU.
        var budget = device.FreeBytes - (long)Math.Ceiling(device.TotalBytes * .03) - kvBytes - 2 * LlamaDenseMemoryPolicy.GiB;
        var used = FixedBytes; var count = 0;
        for (var layer = LayerBytes.Count - 1; layer >= 1; layer--)
        {
            if (LayerBytes[layer] <= 0) throw new InvalidDataException("Invalid layer storage size.");
            if (LayerBytes[layer] > budget - used) break;
            used += LayerBytes[layer]; count++;
        }
        if (count == 0) throw new IOException("Hybrid loading cannot keep a complete layer below the VRAM limit.");
        return count;
    }
}
