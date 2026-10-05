using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed record MusicTokenizerMetadata(string[] Tokens, string[] Merges, int ContextSize)
{
    // Only metadata is read. Tensor data and the inference runtime are not loaded.
    public static MusicTokenizerMetadata Read(string path, CancellationToken cancellation = default)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is not (2 or 3))
            throw new InvalidDataException("Unsupported GGUF header.");
        reader.ReadUInt64();
        var entries = reader.ReadUInt64();
        if (entries > 100_000) throw new InvalidDataException("Invalid GGUF metadata count.");
        string[]? tokens = null, merges = null;
        string? config = null, architecture = null;
        for (ulong i = 0; i < entries; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = ReadString(reader);
            var type = reader.ReadUInt32();
            switch (key)
            {
                case "general.architecture": architecture = StringValue(type); break;
                case "yue2.config_json": config = StringValue(type); break;
                case "tokenizer.ggml.tokens": tokens = StringArray(type); break;
                case "tokenizer.ggml.merges": merges = StringArray(type); break;
                default: Skip(type, 0); break;
            }
            if (stream.Position > 64 * 1024 * 1024) throw new InvalidDataException("GGUF metadata is too large.");
        }
        if (architecture != "yue2" || tokens is null || merges is null || config is null)
            throw new InvalidDataException("YuE2 tokenizer metadata is missing.");
        using var json = JsonDocument.Parse(config);
        var context = json.RootElement.GetProperty("max_position_embeddings").GetInt32();
        if (context != MusicTextBudget.ContextSize) throw new InvalidDataException("Unexpected YuE2 context size.");
        return new(tokens, merges, context);

        string StringValue(uint type) => type == 8 ? ReadString(reader) : throw new InvalidDataException("Expected a string.");
        string[] StringArray(uint type)
        {
            if (type != 9 || reader.ReadUInt32() != 8) throw new InvalidDataException("Expected a string array.");
            var count = reader.ReadUInt64();
            if (count > 1_000_000) throw new InvalidDataException("GGUF array is too large.");
            var result = new string[(int)count];
            for (var j = 0; j < result.Length; j++)
            { cancellation.ThrowIfCancellationRequested(); result[j] = ReadString(reader); }
            return result;
        }
        void Skip(uint type, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 4) throw new InvalidDataException("Invalid nested GGUF array.");
            if (type == 8) { ReadString(reader); return; }
            if (type == 9)
            {
                var element = reader.ReadUInt32(); var count = reader.ReadUInt64();
                if (count > 1_000_000) throw new InvalidDataException("GGUF array is too large.");
                for (ulong j = 0; j < count; j++) Skip(element, depth + 1);
                return;
            }
            var bytes = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8,
                _ => throw new InvalidDataException("Unknown GGUF value type.") };
            if (stream.Position + bytes > stream.Length) throw new EndOfStreamException();
            stream.Seek(bytes, SeekOrigin.Current);
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > 16 * 1024 * 1024 || length > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("Invalid GGUF string length.");
        return new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
    }
}
