using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.VideoPreparation;

/// <summary>Lossless wire-only compression. Runtime snapshots and their revision identities remain unchanged.</summary>
internal static class VideoWorldArchiveSnapshots
{
    internal const string Prefix = "br1:";
    private const int Limit = 8 * 1024 * 1024;
    // Each deserialization gets its own cumulative expansion budget, including concurrent saves.
    internal static JsonSerializerOptions Options => new() { Converters = { new SnapshotConverter() } };
    internal static bool HasHistory(VideoPreparationProject project) => project.World?.History.Count > 0;
    internal static VideoPreparationProject Clone(VideoPreparationProject project) =>
        JsonSerializer.Deserialize<VideoPreparationProject>(JsonSerializer.SerializeToUtf8Bytes(project, Options), Options)!;
    internal static string Pack(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > Limit) throw new InvalidDataException("VideoPrep.ProjectDescriptionTooLarge");
        using var output = new MemoryStream();
        using (var encoder = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) encoder.Write(bytes);
        return Prefix + Convert.ToBase64String(output.ToArray());
    }
    internal static string Unpack(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        if (value.Length > Limit * 2) throw new InvalidDataException("VideoPrep.InvalidArchive");
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(value[Prefix.Length..]));
            using var decoder = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[32768]; int count;
            while ((count = decoder.Read(buffer)) != 0)
            {
                if (output.Length + count > Limit) throw new InvalidDataException("VideoPrep.InvalidArchive");
                output.Write(buffer, 0, count);
            }
            if (output.Length == 0) throw new InvalidDataException("VideoPrep.InvalidArchive");
            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception error) when (error is FormatException or IOException or DecoderFallbackException)
        { throw new InvalidDataException("VideoPrep.InvalidArchive", error); }
    }
    private sealed record Stored(string Id, string Cards, string Context, DateTimeOffset CreatedUtc);
    private sealed class SnapshotConverter : JsonConverter<VideoWorldRevision>
    {
        private long expanded;
        public override VideoWorldRevision? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<Stored>(ref reader, options) ?? throw new InvalidDataException("VideoPrep.InvalidArchive");
            if (value.Cards is null) throw new InvalidDataException("VideoPrep.InvalidArchive");
            var cards = Unpack(value.Cards);
            expanded += Encoding.UTF8.GetByteCount(cards);
            if (expanded > 128L * 1024 * 1024) throw new InvalidDataException("VideoPrep.ProjectDescriptionTooLarge");
            return new(value.Id, cards, value.Context, value.CreatedUtc);
        }
        public override void Write(Utf8JsonWriter writer, VideoWorldRevision value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, new Stored(value.Id, Pack(value.Cards), value.Context, value.CreatedUtc), options);
    }
}
